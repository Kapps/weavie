using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Diffs;
using Weavie.Core.Editor;
using Weavie.Core.Layout;

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
	private readonly IReadOnlyList<string> _workspaceFolders;
	private readonly SettingsStore? _settings;
	private readonly LayoutStore? _layout;
	private readonly EditorStore? _editor;
	private readonly CommandDispatcher? _commands;
	private readonly KeybindingStore? _keybindings;
	private readonly string _toolsListJson;
	private readonly SemaphoreSlim _sendLock = new(1, 1);

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;

	// The currently connected, authenticated client socket — captured so a host-driven active-editor
	// change can push an unsolicited selection_changed notification. volatile: written by the accept
	// task, read from the thread that raises ActiveEditorStore.Changed.
	private volatile WebSocket? _activeWebSocket;

	/// <summary>
	/// Creates the server with the auth token enforced on the WebSocket upgrade, the presenter that
	/// handles inbound tool calls and the advertised workspace folders. When a
	/// <paramref name="settings"/> store is supplied, the settings tools (<c>listSettings</c> /
	/// <c>getSetting</c> / <c>setSetting</c>) are advertised and served. Call <see cref="Start"/> to begin listening.
	/// </summary>
	public McpServer(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName = "weavie",
		SettingsStore? settings = null,
		bool registryMode = false,
		LayoutStore? layout = null,
		EditorStore? editor = null,
		CommandDispatcher? commands = null,
		KeybindingStore? keybindings = null) {
		ArgumentException.ThrowIfNullOrEmpty(authToken);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		if (registryMode && settings is null) {
			throw new ArgumentNullException(nameof(settings), "Registry-mode server requires a settings store.");
		}

		_authToken = authToken;
		_presenter = presenter;
		_workspaceFolders = workspaceFolders;
		_settings = settings;
		_layout = layout;
		_editor = editor;
		_commands = commands;
		_keybindings = keybindings;
		if (editor is not null) {
			// Push an unsolicited selection_changed to the connected client whenever the user's active
			// file/selection changes, so the embedded claude always knows what they're looking at.
			editor.Changed += OnActiveEditorChanged;
		}

		// Registry mode advertises ONLY the capability tools (settings + layout + commands) — this is the
		// model-facing MCP server registered via .mcp.json, kept separate from the IDE server whose
		// openDiff-style tools Claude Code filters out before they reach the model. The default (IDE) mode
		// advertises the IDE RPC tools, plus the settings tools when a store is present.
		string entries;
		if (registryMode) {
			var parts = new List<string> { SettingsToolEntries };
			if (layout is not null) {
				parts.Add(LayoutToolEntries);
			}

			if (commands is not null) {
				parts.Add(CommandToolEntries);
			}

			entries = string.Join(",", parts);
		} else {
			entries = settings is null ? IdeToolEntries : IdeToolEntries + "," + SettingsToolEntries;
		}

		_toolsListJson = "{\"tools\":[" + entries + "]}";
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

				if (!headers.TryGetValue("sec-websocket-key", out string? wsKey)) {
					await WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
					return;
				}

				// CVE-2025-52882: never upgrade without the correct per-session token. Accept it via the
				// IDE header (lock-file discovery) or a standard `Authorization: Bearer` (how Claude Code
				// presents headers for a `.mcp.json` ws server — the capability-registry connection).
				headers.TryGetValue("x-claude-code-ide-authorization", out string? ideToken);
				string? bearer = null;
				if (headers.TryGetValue("authorization", out string? authHeader)
					&& authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
					bearer = authHeader["Bearer ".Length..].Trim();
				}

				if (!string.Equals(ideToken, _authToken, StringComparison.Ordinal)
					&& !string.Equals(bearer, _authToken, StringComparison.Ordinal)) {
					Emit("rejected connection: missing/invalid auth token");
					await WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				string accept = IdeLockFile.ComputeWebSocketAccept(wsKey);
				string response =
					"HTTP/1.1 101 Switching Protocols\r\n" +
					"Upgrade: websocket\r\n" +
					"Connection: Upgrade\r\n" +
					$"Sec-WebSocket-Accept: {accept}\r\n\r\n";
				await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
				await stream.FlushAsync(ct).ConfigureAwait(false);

				using var ws = WebSocket.CreateFromStream(
					stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
				Emit("client connected + authenticated");
				_activeWebSocket = ws;
				ClientConnected?.Invoke();
				try {
					await MessageLoopAsync(ws, ct).ConfigureAwait(false);
				} finally {
					if (ReferenceEquals(_activeWebSocket, ws)) {
						_activeWebSocket = null;
					}
				}
			} catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException) {
				Emit($"client disconnected: {ex.GetType().Name}");
			}
		}
	}

	private async Task MessageLoopAsync(WebSocket ws, CancellationToken ct) {
		byte[] buffer = new byte[16 * 1024];
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

			string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
			message.SetLength(0);

			// Dispatch off the receive loop so a blocking openDiff doesn't stall other messages.
			_ = DispatchAsync(ws, json, ct);
		}
	}

	private async Task DispatchAsync(WebSocket ws, string json, CancellationToken ct) {
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			bool hasId = root.TryGetProperty("id", out var idElement);
			string? idRaw = hasId ? idElement.GetRawText() : null;

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
					await SendResultAsync(ws, idRaw, _toolsListJson, ct).ConfigureAwait(false);
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
		string protocolVersion = "2025-03-26";
		if (root.TryGetProperty("params", out var p) &&
			p.TryGetProperty("protocolVersion", out var pv) &&
			pv.ValueKind == JsonValueKind.String) {
			protocolVersion = pv.GetString() ?? protocolVersion;
		}

		string result = "{\"protocolVersion\":" + JsonString(protocolVersion) +
			",\"capabilities\":{\"tools\":{\"listChanged\":false}},\"serverInfo\":{\"name\":\"weavie\",\"version\":\"0.1.0\"}}";
		await SendResultAsync(ws, idRaw, result, ct).ConfigureAwait(false);
	}

	private async Task HandleToolCallAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
		if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var nameEl)) {
			await SendErrorAsync(ws, idRaw, -32602, "Invalid params", ct).ConfigureAwait(false);
			return;
		}

		string? name = nameEl.GetString();
		p.TryGetProperty("arguments", out var args);
		Emit($"tools/call name={name}");

		switch (name) {
			case "openDiff":
				await HandleOpenDiffAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "openFile":
				string? path = args.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
				if (!string.IsNullOrEmpty(path)) {
					await _presenter.OpenFileAsync(path, ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(ws, idRaw, "FILE_OPENED", ct).ConfigureAwait(false);
				break;
			case "getWorkspaceFolders":
				await SendToolTextAsync(ws, idRaw, BuildWorkspaceFoldersJson(), ct).ConfigureAwait(false);
				break;
			case "getOpenEditors":
				// JSON-stringified {tabs:[...]} in the text item (claudecode.nvim shape). We report the one
				// active editor the page told us about (empty list until then), with the correct object shape.
				await SendToolTextAsync(ws, idRaw, BuildOpenEditorsResult(_editor?.Active), ct).ConfigureAwait(false);
				break;
			case "getCurrentSelection":
			case "getLatestSelection":
				// Stringified {success, text, filePath, selection} in the text item (the shape claude parses).
				await SendToolTextAsync(ws, idRaw, BuildSelectionResult(_editor?.Active), ct).ConfigureAwait(false);
				break;
			case "getDiagnostics":
				await SendResultAsync(ws, idRaw, "{\"content\":[{\"type\":\"text\",\"text\":\"[]\"}]}", ct).ConfigureAwait(false);
				break;
			case "close_tab":
			case "closeAllDiffTabs":
			case "saveDocument":
				await SendToolTextAsync(ws, idRaw, "OK", ct).ConfigureAwait(false);
				break;
			case "listSettings":
				await HandleListSettingsAsync(ws, idRaw, ct).ConfigureAwait(false);
				break;
			case "getSetting":
				await HandleGetSettingAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "setSetting":
				await HandleSetSettingAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "getLayout":
				await HandleGetLayoutAsync(ws, idRaw, ct).ConfigureAwait(false);
				break;
			case "setLayout":
				await HandleSetLayoutAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "listCommands":
				await HandleListCommandsAsync(ws, idRaw, ct).ConfigureAwait(false);
				break;
			case "runCommand":
				await HandleRunCommandAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			default:
				await SendErrorAsync(ws, idRaw, -32601, $"Unknown tool: {name}", ct).ConfigureAwait(false);
				break;
		}
	}

	private async Task HandleListSettingsAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_settings is null) {
			await SendToolErrorAsync(ws, idRaw, "Settings are not available.", ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(ws, idRaw, _settings.BuildCatalogJson(), ct).ConfigureAwait(false);
	}

	private async Task HandleGetSettingAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_settings is null) {
			await SendToolErrorAsync(ws, idRaw, "Settings are not available.", ct).ConfigureAwait(false);
			return;
		}

		string? key = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("key", out var k) ? k.GetString() : null;
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "getSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			await SendToolTextAsync(ws, idRaw, _settings.BuildGetJson(key), ct).ConfigureAwait(false);
		} catch (UnknownSettingException ex) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleSetSettingAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_settings is null) {
			await SendToolErrorAsync(ws, idRaw, "Settings are not available.", ct).ConfigureAwait(false);
			return;
		}

		bool hasArgs = args.ValueKind == JsonValueKind.Object;
		string? key = hasArgs && args.TryGetProperty("key", out var k) ? k.GetString() : null;
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "setSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		if (!hasArgs || !args.TryGetProperty("value", out var valueElement)) {
			await SendToolErrorAsync(ws, idRaw, "setSetting requires a 'value'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			var result = _settings.Set(key, valueElement);
			await SendToolTextAsync(ws, idRaw, FormatSetSummary(key, valueElement, result), ct).ConfigureAwait(false);
			Emit($"setSetting {key} = {valueElement.GetRawText()}");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private static string FormatSetSummary(string key, JsonElement value, SetResult result) {
		string note = result.Apply switch {
			ApplyMode.ReopensTerminal => " The terminal pane will reopen to apply.",
			ApplyMode.NextSession => " It applies to the next session that starts.",
			ApplyMode.RestartRequired => " Restart weavie to apply.",
			_ => " It is live now.",
		};
		string shadow = result.ShadowedByEnv is null
			? string.Empty
			: $" Note: {result.ShadowedByEnv} is set and overrides the file, so the effective value is unchanged until you unset it.";
		return $"Set {key} to {value.GetRawText()}.{note}{shadow}";
	}

	private async Task HandleGetLayoutAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_layout is null) {
			await SendToolErrorAsync(ws, idRaw, "Layout is not available.", ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(ws, idRaw, LayoutSerialization.SerializeCompact(_layout.Current), ct).ConfigureAwait(false);
	}

	private async Task HandleSetLayoutAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_layout is null) {
			await SendToolErrorAsync(ws, idRaw, "Layout is not available.", ct).ConfigureAwait(false);
			return;
		}

		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("root", out var rootElement)) {
			await SendToolErrorAsync(ws, idRaw, "setLayout requires a 'root' layout tree.", ct).ConfigureAwait(false);
			return;
		}

		LayoutNode? root;
		try {
			root = JsonSerializer.Deserialize<LayoutNode>(rootElement.GetRawText(), LayoutSerialization.Options);
		} catch (JsonException ex) {
			await SendToolErrorAsync(ws, idRaw, $"setLayout: invalid root ({ex.Message}).", ct).ConfigureAwait(false);
			return;
		}

		if (root is null) {
			await SendToolErrorAsync(ws, idRaw, "setLayout: 'root' was null.", ct).ConfigureAwait(false);
			return;
		}

		string? focused = args.TryGetProperty("focused", out var focusedElement) ? focusedElement.GetString() : null;
		try {
			var result = _layout.SetPanes(root, focused, LayoutSource.Mcp);
			await SendToolTextAsync(ws, idRaw, result.Summary, ct).ConfigureAwait(false);
			Emit($"setLayout applied ({result.Summary})");
		} catch (LayoutValidationException ex) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleListCommandsAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		if (_commands is null) {
			await SendToolErrorAsync(ws, idRaw, "Commands are not available.", ct).ConfigureAwait(false);
			return;
		}

		// Prefer the keybinding store's catalog (it includes each command's current keys); fall back to the
		// registry alone (no keys) if no keybinding store was wired.
		string commandsArray = _keybindings is not null
			? _keybindings.BuildCommandsJson()
			: CommandCatalog.BuildCommandsArrayJson(_commands.Registry.Definitions, []);
		await SendToolTextAsync(ws, idRaw, $"{{\"commands\":{commandsArray}}}", ct).ConfigureAwait(false);
	}

	private async Task HandleRunCommandAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		if (_commands is null) {
			await SendToolErrorAsync(ws, idRaw, "Commands are not available.", ct).ConfigureAwait(false);
			return;
		}

		bool hasArgs = args.ValueKind == JsonValueKind.Object;
		string? id = hasArgs && args.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
			? idEl.GetString()
			: null;
		if (string.IsNullOrEmpty(id)) {
			await SendToolErrorAsync(ws, idRaw, "runCommand requires an 'id'.", ct).ConfigureAwait(false);
			return;
		}

		// Pass the args object through as raw JSON (the web/core handler coerces leniently); the embedded
		// claude routinely stringifies scalars, so handlers must tolerate that like the settings boundary does.
		string? argsJson = hasArgs && args.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object
			? aEl.GetRawText()
			: null;

		try {
			var result = await _commands.InvokeAsync(id, argsJson, ct).ConfigureAwait(false);
			if (result.Ok) {
				await SendToolTextAsync(ws, idRaw, result.Message ?? $"Ran {id}.", ct).ConfigureAwait(false);
				Emit($"runCommand {id} ok");
			} else {
				await SendToolErrorAsync(ws, idRaw, result.Error ?? $"Command '{id}' failed.", ct).ConfigureAwait(false);
				Emit($"runCommand {id} failed: {result.Error}");
			}
		} catch (UnknownCommandException ex) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private Task SendToolErrorAsync(WebSocket ws, string? idRaw, string text, CancellationToken ct) =>
		SendResultAsync(ws, idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}],\"isError\":true}}", ct);

	private async Task HandleOpenDiffAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		string? GetArg(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) ? v.GetString() : null;

		string? oldPath = GetArg("old_file_path");
		string? newPath = GetArg("new_file_path") ?? oldPath;
		string newContents = GetArg("new_file_contents") ?? string.Empty;
		string tabName = GetArg("tab_name") ?? "Claude Code";

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
			// Conformant openDiff accept: return FILE_SAVED + the (possibly user-edited) final contents and
			// DO NOT write the file — Claude performs the disk write from these returned contents. A
			// server-side write double-writes and desyncs Claude's own permission prompt, which then
			// re-writes onto the already-created file ("file already exists"). Matches coder/claudecode.nvim;
			// see docs/specs/permission-modes-and-change-tracking.md.
			await SendToolTextsAsync(ws, idRaw, ["FILE_SAVED", outcome.FinalContents ?? newContents], ct).ConfigureAwait(false);
			Emit($"openDiff KEEP -> {newPath} (Claude writes)");
		} else {
			await SendToolTextsAsync(ws, idRaw, ["DIFF_REJECTED", tabName], ct).ConfigureAwait(false);
			Emit("openDiff REJECT");
		}
	}

	private Task SendToolTextAsync(WebSocket ws, string? idRaw, string text, CancellationToken ct) =>
		SendResultAsync(ws, idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}]}}", ct);

	// Multi-item text content — the MCP shape Claude expects from openDiff: [FILE_SAVED, <final contents>]
	// on accept, [DIFF_REJECTED, <tab_name>] on reject (matches coder/claudecode.nvim).
	private Task SendToolTextsAsync(WebSocket ws, string? idRaw, IReadOnlyList<string> texts, CancellationToken ct) {
		string items = string.Join(",", texts.Select(t => $"{{\"type\":\"text\",\"text\":{JsonString(t)}}}"));
		return SendResultAsync(ws, idRaw, $"{{\"content\":[{items}]}}", ct);
	}

	// getWorkspaceFolders: a JSON-stringified {success, folders:[{name,uri,path}], rootPath} inside one
	// text item — the shape Claude parses (coder/claudecode.nvim). The earlier ad-hoc top-level
	// "workspaceFolders" field with a "workspace" placeholder text was not read by Claude.
	private string BuildWorkspaceFoldersJson() {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteBoolean("success", true);
			writer.WriteStartArray("folders");
			foreach (string folder in _workspaceFolders) {
				writer.WriteStartObject();
				writer.WriteString("name", Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
				writer.WriteString("uri", PathToFileUri(folder));
				writer.WriteString("path", folder);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteString("rootPath", _workspaceFolders.Count > 0 ? _workspaceFolders[0] : string.Empty);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static string PathToFileUri(string path) {
		try {
			return new Uri(path).AbsoluteUri;
		} catch (UriFormatException) {
			return path;
		}
	}

	// Active-editor context. The page reports the user's active file + selection via the bridge; we both
	// answer getCurrentSelection/getOpenEditors from it and push a selection_changed notification so the
	// embedded claude tracks "what the user is looking at" without having to ask. Matches the VS Code /
	// claudecode.nvim IDE protocol (text/filePath/fileUrl + 0-based selection range).
	private void OnActiveEditorChanged(ActiveEditor active) {
		var ws = _activeWebSocket;
		if (ws is null || ws.State != WebSocketState.Open) {
			return;
		}

		_ = PushSelectionChangedAsync(ws, active);
	}

	private async Task PushSelectionChangedAsync(WebSocket ws, ActiveEditor active) {
		try {
			string notification =
				"{\"jsonrpc\":\"2.0\",\"method\":\"selection_changed\",\"params\":" + BuildSelectionChangedParams(active) + "}";
			await SendRawAsync(ws, notification, CancellationToken.None).ConfigureAwait(false);
			Emit($"selection_changed -> {active.FilePath}");
		} catch (Exception ex) when (ex is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) {
			// Best-effort: the notification is advisory, so a closing/dropped socket just means no push.
		}
	}

	// selection_changed notification params: { text, filePath, fileUrl, selection:{start,end,isEmpty} }.
	private static string BuildSelectionChangedParams(ActiveEditor active) => WriteJson(writer => {
		writer.WriteString("text", active.SelectedText);
		writer.WriteString("filePath", active.FilePath);
		writer.WriteString("fileUrl", PathToFileUri(active.FilePath));
		WriteSelection(writer, active.Selection);
	});

	// getCurrentSelection/getLatestSelection text item: stringified { success, text, filePath, selection }.
	private static string BuildSelectionResult(ActiveEditor? active) => WriteJson(writer => {
		if (active is null) {
			writer.WriteBoolean("success", false);
			writer.WriteString("message", "No active editor");
			return;
		}

		writer.WriteBoolean("success", true);
		writer.WriteString("text", active.SelectedText);
		writer.WriteString("filePath", active.FilePath);
		WriteSelection(writer, active.Selection);
	});

	// getOpenEditors text item: stringified { tabs:[{uri,isActive,label,languageId,isDirty}] } — the one
	// active editor the page reported, or an empty list until it reports one.
	private static string BuildOpenEditorsResult(ActiveEditor? active) => WriteJson(writer => {
		writer.WriteStartArray("tabs");
		if (active is not null) {
			writer.WriteStartObject();
			writer.WriteString("uri", PathToFileUri(active.FilePath));
			writer.WriteBoolean("isActive", true);
			writer.WriteString("label", Path.GetFileName(active.FilePath));
			if (active.LanguageId is not null) {
				writer.WriteString("languageId", active.LanguageId);
			}

			writer.WriteBoolean("isDirty", false);
			writer.WriteEndObject();
		}

		writer.WriteEndArray();
	});

	private static void WriteSelection(Utf8JsonWriter writer, EditorSelection selection) {
		writer.WriteStartObject("selection");
		WritePosition(writer, "start", selection.Start);
		WritePosition(writer, "end", selection.End);
		writer.WriteBoolean("isEmpty", selection.IsEmpty);
		writer.WriteEndObject();
	}

	private static void WritePosition(Utf8JsonWriter writer, string name, EditorPosition position) {
		writer.WriteStartObject(name);
		writer.WriteNumber("line", position.Line);
		writer.WriteNumber("character", position.Character);
		writer.WriteEndObject();
	}

	// Builds a JSON object string: opens/closes the root object around <paramref name="body"/>.
	private static string WriteJson(Action<Utf8JsonWriter> body) {
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			body(writer);
			writer.WriteEndObject();
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private async Task SendResultAsync(WebSocket ws, string? idRaw, string resultJson, CancellationToken ct) {
		if (idRaw is null) {
			return;
		}

		await SendRawAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{resultJson}}}", ct).ConfigureAwait(false);
	}

	private async Task SendErrorAsync(WebSocket ws, string? idRaw, int code, string messageText, CancellationToken ct) {
		string id = idRaw ?? "null";
		await SendRawAsync(ws, $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"error\":{{\"code\":{code},\"message\":{JsonString(messageText)}}}}}", ct).ConfigureAwait(false);
	}

	private async Task SendRawAsync(WebSocket ws, string json, CancellationToken ct) {
		byte[] bytes = Encoding.UTF8.GetBytes(json);
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
		byte[] one = new byte[1];
		int matched = 0; // counts the "\r\n\r\n" terminator
		while (matched < 4) {
			int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
			if (n == 0) {
				break;
			}

			char c = (char)one[0];
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

		string[] lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		foreach (string? line in lines.Skip(1)) // skip the request line
		{
			int colon = line.IndexOf(':', StringComparison.Ordinal);
			if (colon > 0) {
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
			}
		}

		return headers;
	}

	private static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct) {
		string response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	private void Emit(string message) => Log?.Invoke(message);

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if (_editor is not null) {
			_editor.Changed -= OnActiveEditorChanged;
		}

		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		_listener?.Stop();
		_sendLock.Dispose();
	}

	// tools/list entries. openDiff is the star (blocking review); the rest give Claude IDE context.
	// Wrapped in {"tools":[...]} (plus the settings entries when a store is present) by the constructor.
	private const string IdeToolEntries =
		"""
          {"name":"openDiff","description":"Open an editable diff for the user to review proposed changes to a file. Blocks until the user accepts (FILE_SAVED) or rejects (DIFF_REJECTED).","inputSchema":{"type":"object","properties":{"old_file_path":{"type":"string"},"new_file_path":{"type":"string"},"new_file_contents":{"type":"string"},"tab_name":{"type":"string"}},"required":["old_file_path","new_file_path","new_file_contents","tab_name"]}},
          {"name":"openFile","description":"Open/reveal a file in the editor.","inputSchema":{"type":"object","properties":{"filePath":{"type":"string"},"preview":{"type":"boolean"},"startText":{"type":"string"},"endText":{"type":"string"}},"required":["filePath"]}},
          {"name":"getWorkspaceFolders","description":"Get the workspace folders open in the IDE.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getOpenEditors","description":"Get the list of open editor tabs.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getCurrentSelection","description":"Get the current text selection in the active editor.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getDiagnostics","description":"Get language diagnostics from the IDE.","inputSchema":{"type":"object","properties":{"uri":{"type":"string"}}}},
          {"name":"close_tab","description":"Close a tab by name.","inputSchema":{"type":"object","properties":{"tab_name":{"type":"string"}},"required":["tab_name"]}},
          {"name":"closeAllDiffTabs","description":"Close all open diff tabs.","inputSchema":{"type":"object","properties":{}}}
        """;

	// Settings tools (the Claude-facing editing surface), advertised only when a SettingsStore is wired.
	private const string SettingsToolEntries =
		"""
          {"name":"listSettings","description":"List all weavie settings with each one's current value, source (environment/userFile/default), default, description, aliases, and any allowed values. Call this FIRST to find the exact key before changing a setting.","inputSchema":{"type":"object","properties":{}}},
          {"name":"getSetting","description":"Get one weavie setting's resolved value and where it came from.","inputSchema":{"type":"object","properties":{"key":{"type":"string"}},"required":["key"]}},
          {"name":"setSetting","description":"Change a weavie setting. Call listSettings first to find the exact key; never guess keys. 'value' should match the setting's declared type (string/bool/int/path); int and bool values may be sent as a JSON number/boolean or as a string (e.g. 16 or \"16\", true or \"true\").","inputSchema":{"type":"object","properties":{"key":{"type":"string"},"value":{}},"required":["key","value"]}}
        """;

	// Layout tools (model-facing), advertised on the registry server only when a LayoutStore is wired.
	private const string LayoutToolEntries =
		"""
          {"name":"getLayout","description":"Get the current weavie window layout as a JSON tree of nested row/column splits and leaf panes.","inputSchema":{"type":"object","properties":{}}},
          {"name":"setLayout","description":"Replace the weavie window layout. 'root' is a layout tree where each node is a split (type 'split', with 'dir' 'row' or 'column', a 'weights' number array, and a 'children' node array) or a pane (type 'pane', with a unique 'id' and a 'kind'). Pane kinds: editor, terminal:claude, terminal:shell. Weights are relative. Optionally set 'focused' to a pane id. Call getLayout first to see the current shape.","inputSchema":{"type":"object","properties":{"root":{"type":"object"},"focused":{"type":"string"}},"required":["root"]}}
        """;

	// Command tools (model-facing), advertised on the registry server only when a CommandDispatcher is wired.
	private const string CommandToolEntries =
		"""
          {"name":"listCommands","description":"List all weavie commands (actions like focusing a pane, toggling the diff layout, or reopening the terminal) with each one's id, title, category, description, aliases, and current keybinding(s). Call this FIRST to find the exact id before running a command.","inputSchema":{"type":"object","properties":{}}},
          {"name":"runCommand","description":"Run a weavie command by id. Call listCommands first to find the exact id; never guess ids. 'args' is an optional object whose shape depends on the command (e.g. {\"index\":3} to focus the third pane).","inputSchema":{"type":"object","properties":{"id":{"type":"string"},"args":{"type":"object"}},"required":["id"]}}
        """;
}
