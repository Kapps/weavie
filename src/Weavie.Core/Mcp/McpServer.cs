using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Diffs;
using Weavie.Core.Editor;
using Weavie.Core.Json;
using Weavie.Core.Layout;
using Weavie.Core.Theming;

namespace Weavie.Core.Mcp;

/// <summary>
/// The Claude Code "IDE" endpoint: a localhost WebSocket server speaking MCP/JSON-RPC 2.0, into which Claude
/// calls tools (openDiff/openFile/...). Binds 127.0.0.1 only and enforces the per-session auth token on the
/// WebSocket upgrade (mitigating CVE-2025-52882).
/// </summary>
public sealed partial class McpServer : IAsyncDisposable {
	private readonly string _authToken;
	private readonly IDiffPresenter _presenter;
	private readonly IReadOnlyList<string> _workspaceFolders;
	private readonly SettingsStore? _settings;
	private readonly LayoutStore? _layout;
	private readonly EditorStore? _editor;
	private readonly CommandDispatcher? _commands;
	private readonly KeybindingStore? _keybindings;
	private readonly ThemeOverridesStore? _themeOverrides;
	private readonly string _toolsListJson;
	private readonly SemaphoreSlim _sendLock = new(1, 1);

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;

	// The connected, authenticated client socket, so a host-driven active-editor change can push an unsolicited
	// selection_changed. volatile: written by the accept task, read from the thread raising ActiveEditorStore.Changed.
	private volatile WebSocket? _activeWebSocket;

	/// <summary>
	/// Creates the server with the auth token enforced on the WebSocket upgrade, the presenter handling inbound
	/// tool calls, and the advertised workspace folders. A <paramref name="settings"/> store adds the settings
	/// tools. Call <see cref="Start"/> to begin listening.
	/// </summary>
	public McpServer(
		string authToken,
		IDiffPresenter presenter,
		IReadOnlyList<string> workspaceFolders,
		string ideName,
		SettingsStore? settings,
		bool registryMode,
		LayoutStore? layout,
		EditorStore? editor,
		CommandDispatcher? commands,
		KeybindingStore? keybindings,
		ThemeOverridesStore? themeOverrides) {
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
		_themeOverrides = themeOverrides;
		// Push an unsolicited selection_changed to the connected client whenever the user's active
		// file/selection changes, so the embedded claude knows what they're looking at.
		editor?.Changed += OnActiveEditorChanged;

		// Registry mode advertises ONLY the capability tools (the model-facing .mcp.json server), kept separate
		// from the IDE server whose openDiff-style tools Claude Code filters before they reach the model. IDE
		// mode advertises the IDE RPC tools, plus the settings tools when a store is present.
		string entries;
		if (registryMode) {
			var parts = new List<string> { SettingsToolEntries };
			if (layout is not null) {
				parts.Add(LayoutToolEntries);
			}

			if (commands is not null) {
				parts.Add(CommandToolEntries);
			}

			if (themeOverrides is not null) {
				parts.Add(ThemeToolEntries);
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

	/// <summary>Diagnostic log line (handshake, dispatch).</summary>
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
				var request = await WebSocketHandshake.ReadRequestAsync(stream, ct).ConfigureAwait(false);
				if (request is null) {
					return;
				}

				var headers = request.Value.Headers;
				if (!headers.TryGetValue("sec-websocket-key", out string? wsKey)) {
					await WebSocketHandshake.WriteStatusAsync(stream, "400 Bad Request", ct).ConfigureAwait(false);
					return;
				}

				// CVE-2025-52882: never upgrade without the per-session token. Accept it via the IDE header
				// (lock-file discovery) or `Authorization: Bearer` (the .mcp.json ws / capability-registry path).
				headers.TryGetValue("x-claude-code-ide-authorization", out string? ideToken);
				string? bearer = null;
				if (headers.TryGetValue("authorization", out string? authHeader)
					&& authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
					bearer = authHeader["Bearer ".Length..].Trim();
				}

				if (!string.Equals(ideToken, _authToken, StringComparison.Ordinal)
					&& !string.Equals(bearer, _authToken, StringComparison.Ordinal)) {
					Emit("rejected connection: missing/invalid auth token");
					await WebSocketHandshake.WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				await WebSocketHandshake.WriteUpgradeAsync(stream, wsKey, ct).ConfigureAwait(false);

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

	// Returns the registry or reports it as unavailable (caught in HandleToolCallAsync). Lets each tool handler
	// open with `var x = Require(_x, "X");` instead of repeating a null-guard + early SendToolError.
	private static T Require<T>(T? registry, string what) where T : class =>
		registry ?? throw new ToolUnavailableException($"{what} is not available.");

	private async Task HandleInitializeAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
		// Echo the client's protocolVersion (echoing is robust to version disagreement).
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
		try {
			await DispatchToolAsync(ws, root, idRaw, ct).ConfigureAwait(false);
		} catch (ToolUnavailableException ex) {
			// A handler asked for a registry the IDE-mode server wasn't wired with; report it as a tool error.
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task DispatchToolAsync(WebSocket ws, JsonElement root, string? idRaw, CancellationToken ct) {
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
					// `preview` defaults to false (a persistent tab). Coerced leniently: embedded Claude sends
					// scalar args as JSON strings ("true").
					await _presenter.OpenFileAsync(path, ReadLenientBool(args, "preview"), ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(ws, idRaw, "FILE_OPENED", ct).ConfigureAwait(false);
				break;
			case "getWorkspaceFolders":
				await SendToolTextAsync(ws, idRaw, BuildWorkspaceFoldersJson(), ct).ConfigureAwait(false);
				break;
			case "getOpenEditors":
				// JSON-stringified {tabs:[...]} in the text item (claudecode.nvim shape). The page reports the
				// open-tab set via open-editors-changed; empty until it does.
				await SendToolTextAsync(ws, idRaw, BuildOpenEditorsResult(_editor?.OpenEditors, _editor?.Active), ct).ConfigureAwait(false);
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
				// Resolve the tab name (label or path) against the reported open set, then ask the page to
				// close it. Acknowledge "OK" regardless (an unknown tab is a no-op, not an error).
				string? closeName = args.TryGetProperty("tab_name", out var tnEl) ? tnEl.GetString() : null;
				string? closePath = ResolveTabPath(closeName, _editor?.OpenEditors);
				if (closePath is not null) {
					await _presenter.CloseTabAsync(closePath, ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(ws, idRaw, "OK", ct).ConfigureAwait(false);
				break;
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
			case "clearSetting":
				await HandleClearSettingAsync(ws, args, idRaw, ct).ConfigureAwait(false);
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
			case "listThemes":
				await HandleListThemesAsync(ws, idRaw, ct).ConfigureAwait(false);
				break;
			case "describeTheme":
				await HandleDescribeThemeAsync(ws, idRaw, ct).ConfigureAwait(false);
				break;
			case "setThemeOverride":
				await HandleSetThemeOverrideAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "applyThemeTransform":
				await HandleApplyThemeTransformAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "removeThemeOverride":
				await HandleRemoveThemeOverrideAsync(ws, args, idRaw, ct).ConfigureAwait(false);
				break;
			default:
				await SendErrorAsync(ws, idRaw, -32601, $"Unknown tool: {name}", ct).ConfigureAwait(false);
				break;
		}
	}

	private async Task HandleListSettingsAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		await SendToolTextAsync(ws, idRaw, settings.BuildCatalogJson(), ct).ConfigureAwait(false);
	}

	private async Task HandleGetSettingAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "getSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			await SendToolTextAsync(ws, idRaw, settings.BuildGetJson(key), ct).ConfigureAwait(false);
		} catch (UnknownSettingException ex) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleSetSettingAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "setSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("value", out var valueElement)) {
			await SendToolErrorAsync(ws, idRaw, "setSetting requires a 'value'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			var result = settings.Set(key, valueElement);
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

	private async Task HandleClearSettingAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(ws, idRaw, "clearSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			var result = settings.Clear(key);
			await SendToolTextAsync(ws, idRaw, FormatClearSummary(key, result), ct).ConfigureAwait(false);
			Emit($"clearSetting {key} (removed={result.Removed})");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingsFileMalformedException) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private static string FormatClearSummary(string key, ClearResult result) {
		string shadow = result.ShadowedByEnv is null
			? string.Empty
			: $" Note: {result.ShadowedByEnv} is set and overrides the file, so the effective value is unchanged until you unset it.";
		if (!result.Removed) {
			return $"{key} had no user-file override to clear; it was already at its default or env value.{shadow}";
		}

		string note = result.Apply switch {
			ApplyMode.ReopensTerminal => " The terminal pane will reopen to apply.",
			ApplyMode.NextSession => " It applies to the next session that starts.",
			ApplyMode.RestartRequired => " Restart weavie to apply.",
			_ => " It is live now.",
		};
		return $"Cleared {key}; it now falls back to its default.{note}{shadow}";
	}

	private async Task HandleGetLayoutAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		var layout = Require(_layout, "Layout");
		await SendToolTextAsync(ws, idRaw, LayoutSerialization.SerializeCompact(layout.Current), ct).ConfigureAwait(false);
	}

	private async Task HandleSetLayoutAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var layout = Require(_layout, "Layout");
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

		string? focused = args.GetStringOrNull("focused");
		try {
			var result = layout.SetPanes(root, focused, LayoutSource.Mcp);
			await SendToolTextAsync(ws, idRaw, result.Summary, ct).ConfigureAwait(false);
			Emit($"setLayout applied ({result.Summary})");
		} catch (LayoutValidationException ex) {
			await SendToolErrorAsync(ws, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleListCommandsAsync(WebSocket ws, string? idRaw, CancellationToken ct) {
		var commands = Require(_commands, "Commands");
		// Prefer the keybinding store's catalog (it includes each command's current keys); fall back to the
		// registry alone (no keys) when no keybinding store was wired.
		string commandsArray = _keybindings is not null
			? _keybindings.BuildCommandsJson()
			: CommandCatalog.BuildCommandsArrayJson(commands.Registry.Definitions, []);
		await SendToolTextAsync(ws, idRaw, $"{{\"commands\":{commandsArray}}}", ct).ConfigureAwait(false);
	}

	private async Task HandleRunCommandAsync(WebSocket ws, JsonElement args, string? idRaw, CancellationToken ct) {
		var commands = Require(_commands, "Commands");
		bool hasArgs = args.ValueKind == JsonValueKind.Object;
		string? id = args.GetStringOrNull("id");
		if (string.IsNullOrEmpty(id)) {
			await SendToolErrorAsync(ws, idRaw, "runCommand requires an 'id'.", ct).ConfigureAwait(false);
			return;
		}

		// Pass the args object through as raw JSON; the web/core handler coerces leniently, since the embedded
		// claude routinely stringifies scalars.
		string? argsJson = hasArgs && args.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object
			? aEl.GetRawText()
			: null;

		try {
			var result = await commands.InvokeAsync(id, argsJson, ct).ConfigureAwait(false);
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
			// Return FILE_SAVED + the (possibly user-edited) final contents but do NOT write the file: Claude does the
			// disk write; a server-side write double-writes and desyncs Claude's permission prompt.
			// See docs/specs/permission-modes-and-change-tracking.md.
			await SendToolTextsAsync(ws, idRaw, ["FILE_SAVED", outcome.FinalContents ?? newContents], ct).ConfigureAwait(false);
			Emit($"openDiff KEEP -> {newPath} (Claude writes)");
		} else {
			await SendToolTextsAsync(ws, idRaw, ["DIFF_REJECTED", tabName], ct).ConfigureAwait(false);
			Emit("openDiff REJECT");
		}
	}

	private Task SendToolTextAsync(WebSocket ws, string? idRaw, string text, CancellationToken ct) =>
		SendResultAsync(ws, idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}]}}", ct);

	// Multi-item text content — the MCP shape openDiff expects: [FILE_SAVED, <final contents>] on accept,
	// [DIFF_REJECTED, <tab_name>] on reject (matches coder/claudecode.nvim).
	private Task SendToolTextsAsync(WebSocket ws, string? idRaw, IReadOnlyList<string> texts, CancellationToken ct) {
		string items = string.Join(",", texts.Select(t => $"{{\"type\":\"text\",\"text\":{JsonString(t)}}}"));
		return SendResultAsync(ws, idRaw, $"{{\"content\":[{items}]}}", ct);
	}

	// getWorkspaceFolders: a JSON-stringified {success, folders:[{name,uri,path}], rootPath} inside one text
	// item — the shape Claude parses (coder/claudecode.nvim).
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

	// Active-editor context (the page reports the user's active file + selection via the bridge): answers
	// getCurrentSelection/getOpenEditors and pushes selection_changed. VS Code / claudecode.nvim IDE protocol.
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
			// Best-effort: the notification is advisory, so a dropped socket just means no push.
		}
	}

	// selection_changed notification params: { text, filePath, fileUrl, selection:{start,end,isEmpty} }.
	private static string BuildSelectionChangedParams(ActiveEditor active) => JsonWrite.Object(writer => {
		writer.WriteString("text", active.SelectedText);
		writer.WriteString("filePath", active.FilePath);
		writer.WriteString("fileUrl", PathToFileUri(active.FilePath));
		WriteSelection(writer, active.Selection);
	});

	// getCurrentSelection/getLatestSelection text item: stringified { success, text, filePath, selection }.
	private static string BuildSelectionResult(ActiveEditor? active) => JsonWrite.Object(writer => {
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

	// getOpenEditors text item: stringified { tabs:[{uri,isActive,label,languageId,isDirty,isPinned,isPreview}] }.
	// languageId is filled only for the active tab; isDirty is always false (Weavie auto-saves on a debounce).
	private static string BuildOpenEditorsResult(IReadOnlyList<OpenEditorTab>? tabs, ActiveEditor? active) =>
		JsonWrite.Object(writer => {
			writer.WriteStartArray("tabs");
			foreach (var tab in tabs ?? []) {
				writer.WriteStartObject();
				writer.WriteString("uri", PathToFileUri(tab.FilePath));
				writer.WriteBoolean("isActive", tab.IsActive);
				writer.WriteString("label", Path.GetFileName(tab.FilePath));
				if (tab.IsActive && active?.LanguageId is { } languageId) {
					writer.WriteString("languageId", languageId);
				}

				writer.WriteBoolean("isDirty", false);
				writer.WriteBoolean("isPinned", tab.IsPinned);
				writer.WriteBoolean("isPreview", tab.IsPreview);
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		});

	// Resolves a close_tab `tab_name` (a label or full path) to the open tab's path: exact path first, then
	// basename (label), preferring the active tab on a label tie. Null when nothing matches.
	private static string? ResolveTabPath(string? tabName, IReadOnlyList<OpenEditorTab>? tabs) {
		if (string.IsNullOrEmpty(tabName) || tabs is null || tabs.Count == 0) {
			return null;
		}

		foreach (var tab in tabs) {
			if (string.Equals(tab.FilePath, tabName, StringComparison.Ordinal)) {
				return tab.FilePath;
			}
		}

		OpenEditorTab? labelMatch = null;
		foreach (var tab in tabs) {
			if (string.Equals(Path.GetFileName(tab.FilePath), tabName, StringComparison.Ordinal)) {
				if (tab.IsActive) {
					return tab.FilePath;
				}

				labelMatch ??= tab;
			}
		}

		return labelMatch?.FilePath;
	}

	// Reads a boolean MCP arg leniently: a real bool, or the string "true"/"false" (embedded Claude sends
	// scalars as JSON strings). Absent or anything else → false.
	private static bool ReadLenientBool(JsonElement args, string name) {
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) {
			return false;
		}

		return value.ValueKind switch {
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String => string.Equals(value.GetString(), "true", StringComparison.OrdinalIgnoreCase),
			_ => false,
		};
	}

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

	private void Emit(string message) => Log?.Invoke(message);

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		_editor?.Changed -= OnActiveEditorChanged;

		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		_listener?.Stop();
		_sendLock.Dispose();
	}

}
