using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the settings MCP tools (<c>listSettings</c>/<c>getSetting</c>/<c>setSetting</c>) over a
/// loopback WebSocket — the surface the embedded <c>claude</c> uses — covering the path that backs
/// <em>"set my weavie shell to nushell"</em>.
/// In the <c>Settings</c> collection because <c>setSetting</c> resolves shells against the real PATH.
/// </summary>
[Collection("Settings")]
public sealed class McpSettingsToolsTests : IDisposable {
	private const string Token = "0123456789abcdef0123456789abcdef";
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-mcp-settings-tests", Guid.NewGuid().ToString("N"));

	public McpSettingsToolsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	private SettingsStore NewStore() => CoreSettings.CreateStore(FilePath, enableWatcher: false);

	private McpServer NewServer(SettingsStore store) =>
		TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir], "weavie", store);

	// A shell that validates on this machine — prefer nushell (the acceptance target) when present.
	private static string PresentShell() {
		if (ExecutableFinder.FindOnPath("nu") is not null) {
			return "nu";
		}

		return OperatingSystem.IsWindows() ? "cmd" : "/bin/sh";
	}

	[Fact]
	public async Task ToolsList_AdvertisesSettingsTools() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(1, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var names = response.RootElement.GetProperty("result").GetProperty("tools")
			.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("listSettings", names);
		Assert.Contains("getSetting", names);
		Assert.Contains("setSetting", names);
		Assert.Contains("openDiff", names); // IDE tools present too
	}

	[Fact]
	public async Task ListSettings_ReturnsCatalogWithTerminalShell() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(2, "tools/call", "{\"name\":\"listSettings\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string? text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
		using var catalog = JsonDocument.Parse(text!);
		var keys = catalog.RootElement.GetProperty("settings")
			.EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();
		Assert.Contains("terminal.shell", keys);
		Assert.Contains("workspace", keys);
		Assert.Contains("claude.path", keys);
	}

	[Fact]
	public async Task SetSetting_ChangesShell_PersistsAndConfirms() {
		string shell = PresentShell();
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(3, "tools/call",
			$"{{\"name\":\"setSetting\",\"arguments\":{{\"key\":\"terminal.shell\",\"value\":\"{shell}\"}}}}"));
		using var response = await ReceiveAsync(ws);

		var result = response.RootElement.GetProperty("result");
		Assert.False(result.TryGetProperty("isError", out var err) && err.GetBoolean());
		string? text = result.GetProperty("content")[0].GetProperty("text").GetString();
		Assert.Contains("Set terminal.shell", text, StringComparison.Ordinal);
		Assert.Contains("reopen", text, StringComparison.OrdinalIgnoreCase);

		// Persisted to the user file and resolvable.
		Assert.Equal(shell, store.Resolve("terminal.shell").Value);
		Assert.Contains($"terminal.shell = \"{shell}\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SetSetting_BogusShell_ReturnsIsError_AndDoesNotPersist() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(4, "tools/call",
			"{\"name\":\"setSetting\",\"arguments\":{\"key\":\"terminal.shell\",\"value\":\"definitely-not-a-real-shell-xyz\"}}"));
		using var response = await ReceiveAsync(ws);

		var result = response.RootElement.GetProperty("result");
		Assert.True(result.GetProperty("isError").GetBoolean());
		Assert.Equal(SettingSource.Default, store.Resolve("terminal.shell").Source); // unchanged
	}

	[Fact]
	public async Task GetSetting_UnknownKey_ReturnsIsError() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(5, "tools/call", "{\"name\":\"getSetting\",\"arguments\":{\"key\":\"nope\"}}"));
		using var response = await ReceiveAsync(ws);

		Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
	}

	[Fact]
	public async Task GetSetting_WorkspaceScopedKey_ReflectsWorkspaceOverlay() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		// setSetting routes a workspace-scoped key into this session's workspace overlay.
		await SendAsync(ws, Request(6, "tools/call",
			"{\"name\":\"setSetting\",\"arguments\":{\"key\":\"worktree.setupCommand\",\"value\":\"pnpm install\"}}"));
		using (var setResp = await ReceiveAsync(ws)) {
			Assert.False(setResp.RootElement.GetProperty("result").TryGetProperty("isError", out var err) && err.GetBoolean());
		}

		// getSetting must read back that workspace value, not the empty default — the bug this fixes.
		await SendAsync(ws, Request(7, "tools/call",
			"{\"name\":\"getSetting\",\"arguments\":{\"key\":\"worktree.setupCommand\"}}"));
		using var getResp = await ReceiveAsync(ws);

		string? text = getResp.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
		using var doc = JsonDocument.Parse(text!);
		Assert.Equal("pnpm install", doc.RootElement.GetProperty("value").GetString());
		Assert.Equal("workspaceFile", doc.RootElement.GetProperty("source").GetString());
	}

	[Fact]
	public async Task ListSettings_WorkspaceScopedKey_ReflectsWorkspaceOverlay() {
		using var store = NewStore();
		await using var server = NewServer(store);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(8, "tools/call",
			"{\"name\":\"setSetting\",\"arguments\":{\"key\":\"worktree.setupCommand\",\"value\":\"pnpm install\"}}"));
		using (await ReceiveAsync(ws)) { }

		await SendAsync(ws, Request(9, "tools/call", "{\"name\":\"listSettings\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string? text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
		using var catalog = JsonDocument.Parse(text!);
		var setupCommand = catalog.RootElement.GetProperty("settings").EnumerateArray()
			.Single(s => s.GetProperty("key").GetString() == "worktree.setupCommand");
		Assert.Equal("pnpm install", setupCommand.GetProperty("value").GetString());
		Assert.Equal("workspaceFile", setupCommand.GetProperty("source").GetString());
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
