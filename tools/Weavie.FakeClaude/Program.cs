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
Emit("weavie-fake-claude ready");

if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath)) {
	try {
		await RunScriptAsync(scriptPath, mcpConfigPath).ConfigureAwait(false);
	} catch (Exception ex) {
		Emit($"error: {ex.GetType().Name}: {ex.Message}");
	}
}

await BlockOnStdinAsync().ConfigureAwait(false);
return 0;

// Writes a marker to the PTY (so it shows in the claude pane) and, when WEAVIE_FAKE_CLAUDE_LOG is set, to
// that file too — the file is the only channel a test can read directly (the PTY is base64 over the bridge).
static void Emit(string line) {
	Console.Out.Write($"weavie-fake-claude {line}\r\n");
	Console.Out.Flush();
	string? log = Environment.GetEnvironmentVariable("WEAVIE_FAKE_CLAUDE_LOG");
	if (!string.IsNullOrEmpty(log)) {
		File.AppendAllText(log, line + "\n");
	}
}

// Runs each step of the script in order, reusing one authenticated socket per server for the run.
static async Task RunScriptAsync(string scriptPath, string? mcpConfigPath) {
	// Tests can't know the throwaway workspace path when they author the script, so let them write
	// {{WORKSPACE}} and resolve it here to the directory the pane (hence this process) was launched in.
	string text = (await File.ReadAllTextAsync(scriptPath).ConfigureAwait(false))
		.Replace("{{WORKSPACE}}", Directory.GetCurrentDirectory().Replace("\\", "\\\\"), StringComparison.Ordinal);
	using var doc = JsonDocument.Parse(text);
	ClientWebSocket? mcp = null;
	ClientWebSocket? ideMcp = null;
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
					Emit($"edit -> {step.GetProperty("path").GetString()}");
					break;
				case "hook":
					await SendHookAsync(step.GetProperty("request").GetRawText()).ConfigureAwait(false);
					break;
				case "mcp": {
						// "server":"ide" reaches the IDE server (openDiff/openFile); default is the registry server
						// (settings/commands) advertised via --mcp-config.
						bool ide = step.TryGetProperty("server", out var s) && s.GetString() == "ide";
						var conn = ide
							? (ideMcp ??= await ConnectIdeAsync(() => nextId++).ConfigureAwait(false))
							: (mcp ??= await ConnectMcpAsync(mcpConfigPath, () => nextId++).ConfigureAwait(false));
						await CallToolAsync(conn, nextId++, step.GetProperty("tool").GetString()!,
							step.TryGetProperty("args", out var a) ? a.GetRawText() : "{}").ConfigureAwait(false);
						break;
					}
				default:
					Emit($"unknown op: {op}");
					break;
			}
		}
	} finally {
		foreach (var sock in new[] { mcp, ideMcp }) {
			if (sock is not null) {
				await sock.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
				sock.Dispose();
			}
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

	await HandshakeAsync(ws, url, nextId, "mcp").ConfigureAwait(false);
	return ws;
}

// Connects to the IDE MCP server (openDiff/openFile): port from CLAUDE_CODE_SSE_PORT, token from the IDE
// lock file Weavie wrote under the (possibly redirected) Claude config dir.
static async Task<ClientWebSocket> ConnectIdeAsync(Func<int> nextId) {
	string port = Environment.GetEnvironmentVariable("CLAUDE_CODE_SSE_PORT")
		?? throw new InvalidOperationException("CLAUDE_CODE_SSE_PORT not set");
	string configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
		?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
	string lockPath = Path.Combine(configDir, "ide", $"{port}.lock");
	using var lockDoc = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath).ConfigureAwait(false));
	string token = lockDoc.RootElement.GetProperty("authToken").GetString()!;

	var ws = new ClientWebSocket();
	ws.Options.SetRequestHeader("x-claude-code-ide-authorization", token);
	await HandshakeAsync(ws, $"ws://127.0.0.1:{port}/", nextId, "ide").ConfigureAwait(false);
	return ws;
}

static async Task HandshakeAsync(ClientWebSocket ws, string url, Func<int> nextId, string label) {
	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
	await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
	await SendAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{nextId()},\"method\":\"initialize\",\"params\":{{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{{}},\"clientInfo\":{{\"name\":\"fake-claude\",\"version\":\"1.0\"}}}}}}").ConfigureAwait(false);
	await ReceiveAsync(ws).ConfigureAwait(false);
	await SendAsync(ws, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}").ConfigureAwait(false);
	Emit($"{label} connected");
}

static async Task CallToolAsync(ClientWebSocket ws, int id, string tool, string argsJson) {
	await SendAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argsJson}}}}}").ConfigureAwait(false);
	string reply = await ReceiveAsync(ws).ConfigureAwait(false);
	Emit($"{tool} -> {reply}");
}

static async Task SendAsync(ClientWebSocket ws, string json) =>
	await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);

static async Task<string> ReceiveAsync(ClientWebSocket ws) {
	byte[] buffer = new byte[64 * 1024];
	using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
	Emit($"hook -> {Encoding.UTF8.GetString(decision)}");
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
