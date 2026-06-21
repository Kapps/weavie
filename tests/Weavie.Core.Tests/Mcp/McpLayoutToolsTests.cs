using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the layout MCP tools (<c>getLayout</c>/<c>setLayout</c>) over a loopback WebSocket on the
/// registry-mode server — the surface the embedded <c>claude</c> reaches as <c>mcp__weavie__*</c> —
/// covering the path that backs <em>"split my view into left terminal, right editor"</em>.
/// </summary>
public sealed class McpLayoutToolsTests : IDisposable {
	private const string Token = "0123456789abcdef0123456789abcdef";
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-mcp-layout-tests", Guid.NewGuid().ToString("N"));

	public McpLayoutToolsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private McpServer NewRegistryServer(LayoutStore layout) {
		var settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
		return TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", settings, registryMode: true, layout: layout);
	}

	[Fact]
	public async Task ToolsList_AdvertisesLayoutTools() {
		var layout = new LayoutStore(new InMemoryFileSystem(), LayoutPanes.CreateRegistry(), "/layout.json");
		await using var server = NewRegistryServer(layout);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("getLayout", names);
		Assert.Contains("setLayout", names);
		Assert.Contains("setSetting", names); // settings tools advertised alongside
		Assert.DoesNotContain("openDiff", names); // registry mode omits IDE tools
	}

	[Fact]
	public async Task GetLayout_ReturnsCurrentTree() {
		var layout = new LayoutStore(new InMemoryFileSystem(), LayoutPanes.CreateRegistry(), "/layout.json");
		await using var server = NewRegistryServer(layout);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(2, "tools/call", "{\"name\":\"getLayout\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string? text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
		using var doc = JsonDocument.Parse(text!);
		Assert.Equal("split", doc.RootElement.GetProperty("root").GetProperty("type").GetString());
	}

	[Fact]
	public async Task SetLayout_ValidTree_PersistsAndConfirms() {
		var fileSystem = new InMemoryFileSystem();
		var layout = new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json");
		await using var server = NewRegistryServer(layout);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		const string root =
			"{\"type\":\"split\",\"dir\":\"row\",\"weights\":[0.33,0.67],\"children\":["
			+ "{\"type\":\"pane\",\"id\":\"p_shell\",\"kind\":\"terminal:shell\"},"
			+ "{\"type\":\"pane\",\"id\":\"p_editor\",\"kind\":\"editor\"}]}";
		await SendAsync(ws, Request(3, "tools/call",
			$"{{\"name\":\"setLayout\",\"arguments\":{{\"root\":{root},\"focused\":\"p_editor\"}}}}"));
		using var response = await ReceiveAsync(ws);

		var result = response.RootElement.GetProperty("result");
		Assert.False(result.TryGetProperty("isError", out var err) && err.GetBoolean());
		Assert.Equal("p_editor", layout.Current.Focused);

		// Persisted: a reload from the same filesystem sees the change.
		var reloaded = new LayoutStore(fileSystem, LayoutPanes.CreateRegistry(), "/layout.json");
		Assert.Equal("p_editor", reloaded.Current.Focused);
	}

	[Fact]
	public async Task SetLayout_UnknownKind_ReturnsIsError() {
		var layout = new LayoutStore(new InMemoryFileSystem(), LayoutPanes.CreateRegistry(), "/layout.json");
		await using var server = NewRegistryServer(layout);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		const string root = "{\"type\":\"pane\",\"id\":\"x\",\"kind\":\"bogus:kind\"}";
		await SendAsync(ws, Request(4, "tools/call", $"{{\"name\":\"setLayout\",\"arguments\":{{\"root\":{root}}}}}"));
		using var response = await ReceiveAsync(ws);

		Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	private static async Task<ClientWebSocket> ConnectAsync(int port) {
		var client = new ClientWebSocket();
		client.Options.SetRequestHeader("x-claude-code-ide-authorization", Token);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);
		return client;
	}

	private static async Task SendAsync(ClientWebSocket ws, string json) =>
		await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

	private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws) {
		byte[] buffer = new byte[64 * 1024];
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var ms = new MemoryStream();
		WebSocketReceiveResult result;
		do {
			result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
			ms.Write(buffer, 0, result.Count);
		}
		while (!result.EndOfMessage);

		return JsonDocument.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
	}

	private static string Request(int id, string method, string paramsJson) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}";
}
