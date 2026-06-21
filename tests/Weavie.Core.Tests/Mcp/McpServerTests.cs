using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the MCP server with a real loopback WebSocket client (standing in for `claude`) to
/// verify auth enforcement and the JSON-RPC contract deterministically — no real CLI needed.
/// </summary>
public sealed class McpServerTests {
	private const string Token = "0123456789abcdef0123456789abcdef";

	private static McpServer NewServer(IDiffPresenter presenter) =>
		TestMcp.Server(Token, presenter, ["/workspace"]);

	private static async Task<ClientWebSocket> ConnectAsync(int port, string? token) {
		var client = new ClientWebSocket();
		if (token is not null) {
			client.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
		}

		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), cts.Token);
		return client;
	}

	private static async Task SendAsync(ClientWebSocket ws, string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

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

	[Fact]
	public async Task Connection_WithoutToken_IsRejected() {
		await using var server = NewServer(FakeDiffPresenter.AlwaysKeep());
		int port = server.Start();
		await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(port, token: null));
	}

	[Fact]
	public async Task Connection_WithWrongToken_IsRejected() {
		await using var server = NewServer(FakeDiffPresenter.AlwaysKeep());
		int port = server.Start();
		await Assert.ThrowsAsync<WebSocketException>(() => ConnectAsync(port, "wrong-token"));
	}

	[Fact]
	public async Task Initialize_ReturnsServerInfoAndEchoesProtocolVersion() {
		await using var server = NewServer(FakeDiffPresenter.AlwaysKeep());
		int port = server.Start();
		using var ws = await ConnectAsync(port, Token);

		await SendAsync(ws, Request(1, "initialize",
			"{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"claude\",\"version\":\"2.1\"}}"));
		using var response = await ReceiveAsync(ws);

		var result = response.RootElement.GetProperty("result");
		Assert.Equal("2025-03-26", result.GetProperty("protocolVersion").GetString());
		Assert.Equal("weavie", result.GetProperty("serverInfo").GetProperty("name").GetString());
		Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
	}

	[Fact]
	public async Task ToolsList_AdvertisesOpenDiff() {
		await using var server = NewServer(FakeDiffPresenter.AlwaysKeep());
		int port = server.Start();
		using var ws = await ConnectAsync(port, Token);

		await SendAsync(ws, Request(2, "tools/list", "{}"));
		using var response = await ReceiveAsync(ws);

		var tools = response.RootElement.GetProperty("result").GetProperty("tools");
		var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
		Assert.Contains("openDiff", names);
		Assert.Contains("openFile", names);
	}

	[Fact]
	public async Task OpenDiff_Keep_ReturnsFileSavedWithContents() {
		// Conformant accept: content is [FILE_SAVED, <final contents>] and the server does NOT write —
		// Claude performs the disk write from the returned contents.
		await using var server = NewServer(FakeDiffPresenter.AlwaysKeep());
		int port = server.Start();
		using var ws = await ConnectAsync(port, Token);

		await SendAsync(ws, Request(3, "tools/call",
			"{\"name\":\"openDiff\",\"arguments\":{\"old_file_path\":\"/workspace/a.txt\",\"new_file_path\":\"/workspace/a.txt\",\"new_file_contents\":\"new\\n\",\"tab_name\":\"a.txt\"}}"));
		using var response = await ReceiveAsync(ws);

		var content = response.RootElement.GetProperty("result").GetProperty("content");
		Assert.Equal("FILE_SAVED", content[0].GetProperty("text").GetString());
		Assert.Equal("new\n", content[1].GetProperty("text").GetString());
	}

	[Fact]
	public async Task OpenDiff_Reject_ReturnsDiffRejectedWithTabName() {
		await using var server = NewServer(FakeDiffPresenter.AlwaysReject());
		int port = server.Start();
		using var ws = await ConnectAsync(port, Token);

		await SendAsync(ws, Request(4, "tools/call",
			"{\"name\":\"openDiff\",\"arguments\":{\"old_file_path\":\"/workspace/a.txt\",\"new_file_path\":\"/workspace/a.txt\",\"new_file_contents\":\"new\\n\",\"tab_name\":\"a.txt\"}}"));
		using var response = await ReceiveAsync(ws);

		var content = response.RootElement.GetProperty("result").GetProperty("content");
		Assert.Equal("DIFF_REJECTED", content[0].GetProperty("text").GetString());
		Assert.Equal("a.txt", content[1].GetProperty("text").GetString());
	}
}
