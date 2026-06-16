using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Mcp;

/// <summary>
/// The Claude Code "IDE" endpoint: a localhost WebSocket server speaking MCP/JSON-RPC 2.0.
/// Claude connects as the client and calls tools into us (openDiff/openFile/...). We bind
/// 127.0.0.1 only and enforce the per-session auth token on the WebSocket upgrade
/// (mitigating CVE-2025-52882). The protocol is reverse-engineered (coder/claudecode.nvim)
/// and verified empirically against the installed CLI.
/// </summary>
public sealed class McpServer : IAsyncDisposable {
	private readonly string _authToken;
	private readonly IDiffPresenter _presenter;
	private readonly IFileSystem _fileSystem;
	private readonly IReadOnlyList<string> _workspaceFolders;
	private readonly SemaphoreSlim _sendLock = new(1, 1);

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;

	/// <summary>
	/// Creates the server with the auth token enforced on the WebSocket upgrade, the presenter that
	/// handles inbound tool calls, the filesystem seam, and the advertised workspace folders.
	/// Call <see cref="Start"/> to begin listening.
	/// </summary>
	public McpServer(
		string authToken,
		IDiffPresenter presenter,
		IFileSystem fileSystem,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie") {
		ArgumentException.ThrowIfNullOrEmpty(authToken);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(workspaceFolders);

		_authToken = authToken;
		_presenter = presenter;
		_fileSystem = fileSystem;
		_workspaceFolders = workspaceFolders;
		IdeName = ideName;
	}

	/// <summary>The IDE name reported to Claude during the handshake.</summary>
	public string IdeName { get; }

	/// <summary>The loopback port the server is listening on; 0 until <see cref="Start"/> is called.</summary>
	public int Port { get; private set; }

	/// <summary>Diagnostic log line (handshake, dispatch). Used to verify the protocol empirically.</summary>
	public event Action<string>? Log;

	/// <summary>Raised when an authenticated client completes the WebSocket upgrade.</summary>
	public event Action? ClientConnected;

	/// <summary>Binds an ephemeral loopback port, starts accepting, and returns the port.</summary>
	public int Start() {
		if (_listener is not null) {
			throw new InvalidOperationException("Server already started.");
		}

		_listener = new TcpListener(IPAddress.Loopback, 0);
		_listener.Start();
		Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
		_cts = new CancellationTokenSource();
		_ = AcceptLoopAsync(_cts.Token);
		Emit($"listening on 127.0.0.1:{Port}");
		return Port;
	}

	private async Task AcceptLoopAsync(CancellationToken ct) {
		while (!ct.IsCancellationRequested) {
			TcpClient client;
			try {
				client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			} catch (ObjectDisposedException) {
				break;
			}

			_ = HandleClientAsync(client, ct);
		}
	}

	private async Task HandleClientAsync(TcpClient client, CancellationToken ct) {
		using (client) {
			client.NoDelay = true;
			var stream = client.GetStream();
			try {
				var headers = await ReadHttpHeadersAsync(stream, ct).ConfigureAwait(false);

				if (!headers.TryGetValue("sec-websocket-key", out var wsKey)) {
					await WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
					return;
				}

				// CVE-2025-52882: never upgrade without the correct per-session token.
				headers.TryGetValue("x-claude-code-ide-authorization", out var token);
				if (!string.Equals(token, _authToken, StringComparison.Ordinal)) {
					Emit("rejected connection: missing/invalid auth token");
					await WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				var accept = IdeLockFile.ComputeWebSocketAccept(wsKey);
				var response =
					"HTTP/1.1 101 Switching Protocols\r\n" +
					"Upgrade: websocket\r\n" +
					"Connection: Upgrade\r\n" +
					$"Sec-WebSocket-Accept: {accept}\r\n\r\n";
				await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
				await stream.FlushAsync(ct).ConfigureAwait(false);

				using var ws = WebSocket.CreateFromStream(
					stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
				Emit("client connected + authenticated");
				ClientConnected?.Invoke();
				await MessageLoopAsync(ws, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) {
				Emit($"client disconnected: {ex.GetType().Name}");
			}
		}
	}

	private async Task MessageLoopAsync(WebSocket ws, CancellationToken ct) {
		var buffer = new byte[16 * 1024];
		var message = new MemoryStream();
		while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested) {
			WebSocketReceiveResult result;
			try {
				result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) {
				break;
			}

			if (result.MessageType == WebSocketMessageType.Close) {
				await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
				break;
			}

			message.Write(buffer, 0, result.Count);
			if (!result.EndOfMessage) {
				continue;
			}

			var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
			message.SetLength(0);

			// Dispatch off the receive loop so a blocking openDiff doesn't stall other messages.
			_ = DispatchAsync(ws, json, ct);
		}
	}

	private async Task DispatchAsync(WebSocket ws, string json, CancellationToken ct) {
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			var hasId = root.TryGetProperty("id", out var idElement);
			var idRaw = hasId ? idElement.GetRawText() : null;

			Emit($"recv: method={method ?? "(response)"} id={idRaw ?? "-"}");

			if (method is null) {
				return; // a response to one of our (rare) requests; nothing to do
			}

			switch (method) {
				case "initialize":
					await HandleInitializeAsync(ws, root, idRaw, ct).ConfigureAwait(false);
					break;
				case "notifications/initialized":
				case "notifications/cancelled":
					break; // notifications: no reply
				case "ping":
					await SendResultAsync(ws, idRaw, "{}", ct).ConfigureAwait(false);
					break;
				case "tools/list":
					await SendResultAsync(ws, idRaw, ToolsListJson, ct).ConfigureAwait(false);
					break;
				case "tools/call":
					await HandleToolCallAsync(ws, root, idRaw, ct).ConfigureAwait(false);
					break;
				default:
					if (idRaw is not null) {
						await SendErrorAsync(ws, idRaw, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
					}

					break;
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			Emit($"dispatch error: {ex.Message}");
		}
	}

	private async Task HandleInitializeAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
		// Echo the client's protocolVersion (sources disagree on the exact value; echoing is robust).
		var protocolVersion = "2025-03-26";
		if (root.TryGetProperty("params", out var p) &&
			p.TryGetProperty("protocolVersion", out var pv) &&
			pv.ValueKind == JsonValueKind.String) {
			protocolVersion = pv.GetString() ?? protocolVersion;
		}

		var result = "{\"protocolVersion\":" + JsonString(protocolVersion) +
			",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"weavie\",\"version\":\"0.1.0\"}}";
		await SendResultAsync(ws, idRaw, result, ct).ConfigureAwait(false);
	}

	private async Task HandleToolCallAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
		if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var nameEl)) {
			await SendErrorAsync(ws, idRaw, -32602, "Invalid params", ct).ConfigureAwait(false);
			return;
		}

		var name = nameEl.GetString();
		p.TryGetProperty("arguments", out var args);
		Emit($"tools/call name={name}");

		switch (name) {
			case "openDiff":
				await HandleOpenDiffAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "openFile":
				var path = args.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
				if (!string.IsNullOrEmpty(path)) {
					await _presenter.OpenFileAsync(path, ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(ws, idRaw, "FILE_OPENED", ct).ConfigureAwait(false);
				break;
			case "getWorkspaceFolders":
				var folders = string.Join(",", _workspaceFolders.Select(JsonString));
				await SendResultAsync(ws, idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":\"workspace\"}}],\"workspaceFolders\":[{folders}]}}", ct).ConfigureAwait(false);
				break;
			case "getOpenEditors":
				await SendResultAsync(ws, idRaw, "{\"content\":[{\"type\":\"text\",\"text\":\"[]\"}]}", ct).ConfigureAwait(false);
				break;
			case "getCurrentSelection":
			case "getLatestSelection":
				await SendResultAsync(ws, idRaw, "{\"content\":[{\"type\":\"text\",\"text\":\"\"}]}", ct).ConfigureAwait(false);
				break;
			case "getDiagnostics":
				await SendResultAsync(ws, idRaw, "{\"content\":[{\"type\":\"text\",\"text\":\"[]\"}]}", ct).ConfigureAwait(false);
				break;
			case "close_tab":
			case "closeAllDiffTabs":
			case "saveDocument":
				await SendToolTextAsync(ws, idRaw, "OK", ct).ConfigureAwait(false);
				break;
			default:
				await SendErrorAsync(ws, idRaw, -32601, $"Unknown tool: {name}", ct).ConfigureAwait(false);
				break;
		}
	}

	private async Task HandleOpenDiffAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		string? GetArg(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) ? v.GetString() : null;

		var oldPath = GetArg("old_file_path");
		var newPath = GetArg("new_file_path") ?? oldPath;
		var newContents = GetArg("new_file_contents") ?? string.Empty;
		var tabName = GetArg("tab_name") ?? "Claude Code";

		if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) {
			await SendErrorAsync(ws, idRaw, -32602, "openDiff requires old_file_path/new_file_path", ct).ConfigureAwait(false);
			return;
		}

		var proposal = new DiffProposal(oldPath, newPath, newContents, tabName);
		DiffOutcome outcome;
		try {
			outcome = await _presenter.PresentDiffAsync(proposal, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) {
			return;
		}

		if (outcome.Result == DiffResult.Kept) {
			// The IDE persists the (possibly user-edited) contents, then reports FILE_SAVED.
			_fileSystem.WriteAllText(newPath, outcome.FinalContents ?? newContents);
			await SendToolTextAsync(ws, idRaw, "FILE_SAVED", ct).ConfigureAwait(false);
			Emit($"openDiff KEEP -> saved {newPath}");
		} else {
			await SendToolTextAsync(ws, idRaw, "DIFF_REJECTED", ct).ConfigureAwait(false);
			Emit("openDiff REJECT");
		}
	}

	private Task SendToolTextAsync(WebSocket ws, string? idRaw, string text, CancellationToken ct) =>
		SendResultAsync(ws, idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}]}}", ct);

	private async Task SendResultAsync(WebSocket ws, string? idRaw, string resultJson, CancellationToken ct) {
		if (idRaw is null) {
			return;
		}

		await SendRawAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}", ct).ConfigureAwait(false);
	}

	private async Task SendErrorAsync(WebSocket ws, string? idRaw, int code, string messageText, CancellationToken ct) {
		var id = idRaw ?? "null";
		await SendRawAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":{JsonString(messageText)}}}}}", ct).ConfigureAwait(false);
	}

	private async Task SendRawAsync(WebSocket ws, string json, CancellationToken ct) {
		var bytes = Encoding.UTF8.GetBytes(json);
		await _sendLock.WaitAsync(ct).ConfigureAwait(false);
		try {
			await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
		} finally {
			_sendLock.Release();
		}
	}

	private static async Task<Dictionary<string, string>> ReadHttpHeadersAsync(NetworkStream stream, CancellationToken ct) {
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var sb = new StringBuilder();
		var one = new byte[1];
		var matched = 0; // counts the "\r\n\r\n" terminator
		while (matched < 4) {
			var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
			if (n == 0) {
				break;
			}

			var c = (char)one[0];
			sb.Append(c);
			matched = c switch {
				'\r' when matched is 0 or 2 => matched + 1,
				'\n' when matched is 1 or 3 => matched + 1,
				_ => 0,
			};

			if (sb.Length > 64 * 1024) {
				break; // header flood guard
			}
		}

		var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		foreach (var line in lines.Skip(1)) // skip the request line
		{
			var colon = line.IndexOf(':', StringComparison.Ordinal);
			if (colon > 0) {
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
			}
		}

		return headers;
	}

	private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct) {
		var response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private void Emit(string message) => Log?.Invoke(message);

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		_listener?.Stop();
		_sendLock.Dispose();
	}

	// tools/list payload. openDiff is the star (blocking review); the rest give Claude IDE context.
	private const string ToolsListJson =
		"""
        {"tools":[
          {"name":"openDiff","description":"Open an editable diff for the user to review proposed changes to a file. Blocks until the user accepts (FILE_SAVED) or rejects (DIFF_REJECTED).","inputSchema":{"type":"object","properties":{"old_file_path":{"type":"string"},"new_file_path":{"type":"string"},"new_file_contents":{"type":"string"},"tab_name":{"type":"string"}},"required":["old_file_path","new_file_path","new_file_contents","tab_name"]}},
          {"name":"openFile","description":"Open/reveal a file in the editor.","inputSchema":{"type":"object","properties":{"filePath":{"type":"string"},"preview":{"type":"boolean"},"startText":{"type":"string"},"endText":{"type":"string"}},"required":["filePath"]}},
          {"name":"getWorkspaceFolders","description":"Get the workspace folders open in the IDE.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getOpenEditors","description":"Get the list of open editor tabs.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getCurrentSelection","description":"Get the current text selection in the active editor.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getDiagnostics","description":"Get language diagnostics from the IDE.","inputSchema":{"type":"object","properties":{"uri":{"type":"string"}}}},
          {"name":"close_tab","description":"Close a tab by name.","inputSchema":{"type":"object","properties":{"tab_name":{"type":"string"}},"required":["tab_name"]}},
          {"name":"closeAllDiffTabs","description":"Close all open diff tabs.","inputSchema":{"type":"object","properties":{}}}
        ]}
        """;
}
