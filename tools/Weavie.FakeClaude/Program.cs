using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Hooks;

// Fake `claude` for integration tests. Weavie spawns it in place of the real CLI (via the claude.path
// setting), so a journey through the whole stack stays deterministic and spends no model usage. With no
// script it just prints a banner and stays alive (a healthy child for the terminal supervisor); a script
// file named by WEAVIE_FAKE_CLAUDE_SCRIPT drives Weavie's MCP + hook seams step by step.

string? mcpConfigPath = ArgValue(args, "--mcp-config");
string? scriptPath = Environment.GetEnvironmentVariable("WEAVIE_FAKE_CLAUDE_SCRIPT");

// The banner doubles as the startup marker a test can wait for in the claude pane.
Console.Out.Write("weavie-fake-claude ready\r\n");
Console.Out.Flush();

if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath)) {
	try {
		await RunScriptAsync(scriptPath, mcpConfigPath).ConfigureAwait(false);
	} catch (Exception ex) {
		Console.Out.Write($"weavie-fake-claude error: {ex.Message}\r\n");
		Console.Out.Flush();
	}
}

await BlockOnStdinAsync().ConfigureAwait(false);
return 0;

// Runs each step of the script in order; an MCP step reuses one authenticated WebSocket for the run.
static async Task RunScriptAsync(string scriptPath, string? mcpConfigPath) {
	using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(scriptPath).ConfigureAwait(false));
	ClientWebSocket? mcp = null;
	int nextId = 1;
	try {
		foreach (var step in doc.RootElement.EnumerateArray()) {
			string op = step.GetProperty("op").GetString() ?? "";
			switch (op) {
				case "print":
					Console.Out.Write(step.GetProperty("text").GetString());
					Console.Out.Flush();
					break;
				case "sleep":
					await Task.Delay(step.GetProperty("ms").GetInt32()).ConfigureAwait(false);
					break;
				case "edit":
					await File.WriteAllTextAsync(step.GetProperty("path").GetString()!, step.GetProperty("content").GetString()).ConfigureAwait(false);
					Console.Out.Write($"weavie-fake-claude edit -> {step.GetProperty("path").GetString()}\r\n");
					Console.Out.Flush();
					break;
				case "hook":
					await SendHookAsync(step.GetProperty("request").GetRawText()).ConfigureAwait(false);
					break;
				case "mcp":
					mcp ??= await ConnectMcpAsync(mcpConfigPath, () => nextId++).ConfigureAwait(false);
					await CallToolAsync(mcp, nextId++, step.GetProperty("tool").GetString()!,
						step.TryGetProperty("args", out var a) ? a.GetRawText() : "{}").ConfigureAwait(false);
					break;
				default:
					Console.Out.Write($"weavie-fake-claude unknown op: {op}\r\n");
					Console.Out.Flush();
					break;
			}
		}
	} finally {
		if (mcp is not null) {
			await mcp.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
			mcp.Dispose();
		}
	}
}

// Connects to the registry MCP server named in the --mcp-config file, replicating its headers verbatim
// (the token lives there), then performs the MCP initialize handshake.
static async Task<ClientWebSocket> ConnectMcpAsync(string? mcpConfigPath, Func<int> nextId) {
	if (string.IsNullOrEmpty(mcpConfigPath) || !File.Exists(mcpConfigPath)) {
		throw new InvalidOperationException($"--mcp-config not found: {mcpConfigPath}");
	}

	using var cfg = JsonDocument.Parse(await File.ReadAllTextAsync(mcpConfigPath).ConfigureAwait(false));
	var server = cfg.RootElement.GetProperty("mcpServers").EnumerateObject().First().Value;
	string url = server.GetProperty("url").GetString()!;

	var ws = new ClientWebSocket();
	if (server.TryGetProperty("headers", out var headers)) {
		foreach (var header in headers.EnumerateObject()) {
			ws.Options.SetRequestHeader(header.Name, header.Value.GetString());
		}
	}

	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
	await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);

	await SendAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{nextId()},\"method\":\"initialize\",\"params\":{{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{{}},\"clientInfo\":{{\"name\":\"fake-claude\",\"version\":\"1.0\"}}}}}}").ConfigureAwait(false);
	await ReceiveAsync(ws).ConfigureAwait(false);
	await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").ConfigureAwait(false);
	Console.Out.Write("weavie-fake-claude mcp connected\r\n");
	Console.Out.Flush();
	return ws;
}

static async Task CallToolAsync(ClientWebSocket ws, int id, string tool, string argsJson) {
	await SendAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argsJson}}}}}").ConfigureAwait(false);
	string reply = await ReceiveAsync(ws).ConfigureAwait(false);
	Console.Out.Write($"weavie-fake-claude mcp {tool} -> {reply}\r\n");
	Console.Out.Flush();
}

static async Task SendAsync(ClientWebSocket ws, string json) =>
	await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

static async Task<string> ReceiveAsync(ClientWebSocket ws) {
	byte[] buffer = new byte[64 * 1024];
	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
	using var ms = new MemoryStream();
	WebSocketReceiveResult result;
	do {
		result = await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
		ms.Write(buffer, 0, result.Count);
	}
	while (!result.EndOfMessage);
	return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
}

// Dials Weavie's hook bridge directly over the named pipe (the same path the real relay uses), sending
// one framed hook request and echoing the decision — reusing Weavie.Core's wire helpers.
static async Task SendHookAsync(string requestJson) {
	string? pipe = Environment.GetEnvironmentVariable(HookProtocol.PipeEnvVar);
	if (string.IsNullOrEmpty(pipe)) {
		throw new InvalidOperationException($"{HookProtocol.PipeEnvVar} not set");
	}

	using var client = new NamedPipeClientStream(
		".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
	await client.ConnectAsync(5000).ConfigureAwait(false);
	await HookProtocol.WriteFramedAsync(client, Encoding.UTF8.GetBytes(requestJson), CancellationToken.None).ConfigureAwait(false);
	byte[] decision = await HookProtocol.ReadFramedAsync(client, CancellationToken.None).ConfigureAwait(false) ?? [];
	Console.Out.Write($"weavie-fake-claude hook -> {Encoding.UTF8.GetString(decision)}\r\n");
	Console.Out.Flush();
}

// Reads stdin until the PTY closes, so the process lives like an interactive TUI (the supervisor expects a
// long-lived child) and exits cleanly only when Weavie tears the pane down.
static async Task BlockOnStdinAsync() {
	await using var stdin = Console.OpenStandardInput();
	byte[] buffer = new byte[1024];
	while (await stdin.ReadAsync(buffer).ConfigureAwait(false) > 0) {
		// Discard keystrokes; a real TUI would render them.
	}
}

static string? ArgValue(string[] args, string name) {
	int i = Array.IndexOf(args, name);
	return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
