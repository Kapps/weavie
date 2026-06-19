using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Editor;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the IDE-mode MCP server's active-editor surface end to end over a real loopback WebSocket:
/// the <c>getCurrentSelection</c>/<c>getOpenEditors</c> tools read the editor store, and a store change
/// pushes an unsolicited <c>selection_changed</c> notification — how the embedded claude learns what the
/// user is looking at.
/// </summary>
public sealed class McpActiveEditorToolsTests {
	private const string Token = "0123456789abcdef0123456789abcdef";

	// An OS-native absolute path so PathToFileUri (new Uri(path).AbsoluteUri) yields a real file:// URL.
	private static readonly string FilePath = OperatingSystem.IsWindows() ? @"C:\workspace\a.cs" : "/workspace/a.cs";
	private static readonly string OtherPath = OperatingSystem.IsWindows() ? @"C:\workspace\b.cs" : "/workspace/b.cs";

	private static McpServer NewServer(EditorStore editor) =>
		new(Token, FakeDiffPresenter.AlwaysKeep(), ["/workspace"], "weavie", editor: editor);

	[Fact]
	public async Task GetCurrentSelection_NoActiveEditor_ReturnsSuccessFalse() {
		await using var server = NewServer(new EditorStore());
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(1, "tools/call", "{\"name\":\"getCurrentSelection\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		using var payload = JsonDocument.Parse(text);
		Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
	}

	[Fact]
	public async Task GetCurrentSelection_WithActiveEditor_ReturnsTextPathAndSelection() {
		var editor = new EditorStore();
		await using var server = NewServer(editor);
		int port = server.Start();
		// Set the active editor BEFORE connecting: a change while connected would push an unsolicited
		// selection_changed notification ahead of the tool reply. The tool reads the stored state.
		editor.SetActive(new ActiveEditor(
			FilePath, "csharp", "hello", new EditorSelection(new EditorPosition(2, 0), new EditorPosition(2, 5), IsEmpty: false)));
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(2, "tools/call", "{\"name\":\"getCurrentSelection\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		using var payload = JsonDocument.Parse(text);
		var root = payload.RootElement;
		Assert.True(root.GetProperty("success").GetBoolean());
		Assert.Equal("hello", root.GetProperty("text").GetString());
		Assert.Equal(FilePath, root.GetProperty("filePath").GetString());
		Assert.Equal(2, root.GetProperty("selection").GetProperty("start").GetProperty("line").GetInt32());
		Assert.False(root.GetProperty("selection").GetProperty("isEmpty").GetBoolean());
	}

	[Fact]
	public async Task GetOpenEditors_ReturnsReportedOpenSetWithFlags() {
		var editor = new EditorStore();
		await using var server = NewServer(editor);
		int port = server.Start();
		// getOpenEditors reflects the open-tab set the page reported; languageId for the active tab is
		// filled from the active-editor report.
		editor.SetActive(new ActiveEditor(
			FilePath, "csharp", string.Empty, new EditorSelection(default, default, IsEmpty: true)));
		editor.SetOpenEditors([
			new OpenEditorTab(FilePath, IsActive: true, IsPinned: false, IsPreview: false),
			new OpenEditorTab(OtherPath, IsActive: false, IsPinned: true, IsPreview: false),
		]);
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(3, "tools/call", "{\"name\":\"getOpenEditors\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		using var payload = JsonDocument.Parse(text);
		var tabs = payload.RootElement.GetProperty("tabs");
		Assert.Equal(2, tabs.GetArrayLength());
		Assert.Equal("a.cs", tabs[0].GetProperty("label").GetString());
		Assert.Equal("csharp", tabs[0].GetProperty("languageId").GetString());
		Assert.True(tabs[0].GetProperty("isActive").GetBoolean());
		Assert.False(tabs[0].GetProperty("isPinned").GetBoolean());
		Assert.False(tabs[1].GetProperty("isActive").GetBoolean());
		Assert.True(tabs[1].GetProperty("isPinned").GetBoolean());
	}

	[Fact]
	public async Task GetOpenEditors_NoReportedSet_ReturnsEmpty() {
		await using var server = NewServer(new EditorStore());
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		await SendAsync(ws, Request(3, "tools/call", "{\"name\":\"getOpenEditors\",\"arguments\":{}}"));
		using var response = await ReceiveAsync(ws);

		string text = response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
		using var payload = JsonDocument.Parse(text);
		Assert.Equal(0, payload.RootElement.GetProperty("tabs").GetArrayLength());
	}

	[Fact]
	public async Task CloseTab_ResolvesNameAndAsksPresenterToClose() {
		var fake = FakeDiffPresenter.AlwaysKeep();
		var editor = new EditorStore();
		await using var server = new McpServer(Token, fake, ["/workspace"], "weavie", editor: editor);
		int port = server.Start();
		editor.SetOpenEditors([new OpenEditorTab(FilePath, IsActive: true, IsPinned: false, IsPreview: false)]);
		using var ws = await ConnectAsync(port);

		// close_tab takes the label; it resolves to the tab's path and asks the presenter to close it.
		await SendAsync(ws, Request(4, "tools/call", "{\"name\":\"close_tab\",\"arguments\":{\"tab_name\":\"a.cs\"}}"));
		using var response = await ReceiveAsync(ws);

		Assert.Equal("OK", response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
		Assert.Equal(FilePath, Assert.Single(fake.ClosedTabs));
	}

	[Fact]
	public async Task SetActive_PushesSelectionChangedNotification() {
		var editor = new EditorStore();
		await using var server = NewServer(editor);
		int port = server.Start();
		using var ws = await ConnectAsync(port);

		// Round-trip initialize first so the server is in its message loop and has captured the socket
		// (the unsolicited push targets the connected client; the handshake guarantees it's wired).
		await SendAsync(ws, Request(1, "initialize",
			"{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"claude\",\"version\":\"2.1\"}}"));
		using (await ReceiveAsync(ws)) {
		}

		editor.SetActive(new ActiveEditor(
			FilePath, "csharp", "sel", new EditorSelection(new EditorPosition(1, 2), new EditorPosition(1, 5), IsEmpty: false)));

		using var notification = await ReceiveAsync(ws);
		var root = notification.RootElement;
		Assert.False(root.TryGetProperty("id", out _)); // a notification has no id
		Assert.Equal("selection_changed", root.GetProperty("method").GetString());
		var p = root.GetProperty("params");
		Assert.Equal("sel", p.GetProperty("text").GetString());
		Assert.Equal(FilePath, p.GetProperty("filePath").GetString());
		Assert.StartsWith("file://", p.GetProperty("fileUrl").GetString());
		Assert.Equal(1, p.GetProperty("selection").GetProperty("start").GetProperty("line").GetInt32());
		Assert.False(p.GetProperty("selection").GetProperty("isEmpty").GetBoolean());
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
