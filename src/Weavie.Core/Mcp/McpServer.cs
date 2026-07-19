using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
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
	// The WebSocket subprotocol Claude Code offers on both its ws-ide and ws MCP transports.
	private const string McpSubProtocol = "mcp";

	private readonly string _authToken;
	private readonly IDiffPresenter _presenter;
	private readonly IReadOnlyList<string> _workspaceFolders;
	private readonly SettingsStore? _settings;
	private readonly LayoutStore? _layout;
	private readonly EditorStore? _editor;
	private readonly CommandDispatcher? _commands;
	private readonly KeybindingStore? _keybindings;
	private readonly ThemeOverridesStore? _themeOverrides;
	private readonly Func<string>? _currentSessionId;
	private readonly string _toolsListJson;
	private readonly IReadOnlyList<McpPrompt> _prompts;
	private readonly SemaphoreSlim _sendLock = new(1, 1);

	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private int _disposed;

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
		bool exposeIdeTools,
		LayoutStore? layout,
		EditorStore? editor,
		CommandDispatcher? commands,
		KeybindingStore? keybindings,
		ThemeOverridesStore? themeOverrides,
		Func<string>? currentSessionId) {
		ArgumentException.ThrowIfNullOrEmpty(authToken);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(workspaceFolders);
		if (registryMode && settings is null) {
			throw new ArgumentNullException(nameof(settings), "Registry-mode server requires a settings store.");
		}
		if (registryMode && exposeIdeTools && editor is null) {
			throw new ArgumentNullException(nameof(editor), "A registry exposing IDE tools requires an editor store.");
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
		_currentSessionId = currentSessionId;
		// Push an unsolicited selection_changed to the connected client whenever the user's active
		// file/selection changes, so the embedded claude knows what they're looking at.
		editor?.Changed += OnActiveEditorChanged;

		// Registry mode advertises ONLY the capability tools (the model-facing .mcp.json server), kept separate
		// from the IDE server whose openDiff-style tools Claude Code filters before they reach the model. IDE
		// mode advertises the IDE RPC tools, plus the settings tools when a store is present.
		string entries;
		if (registryMode) {
			var parts = new List<string> { SettingsToolEntries };
			if (exposeIdeTools) {
				parts.Add(IdeToolEntries);
			}

			if (currentSessionId is not null) {
				parts.Add(CurrentSessionToolEntries);
			}

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
		// Prompts (surfaced by Claude Code as /mcp__weavie__<name> slash commands) are a registry-mode capability.
		_prompts = registryMode ? RegistryPrompts : [];
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

				var headers = request.Headers;
				if (!headers.TryGetValue("sec-websocket-key", out string? wsKey)) {
					await HandleHttpClientAsync(stream, request, ct).ConfigureAwait(false);
					return;
				}

				if (!IsAuthorized(headers)) {
					Emit("rejected connection: missing/invalid auth token");
					await WebSocketHandshake.WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
					return;
				}

				string? subProtocol = WebSocketHandshake.SelectSubProtocol(headers, McpSubProtocol);
				await WebSocketHandshake.WriteUpgradeAsync(stream, wsKey, subProtocol, ct).ConfigureAwait(false);

				using var ws = WebSocket.CreateFromStream(
					stream, isServer: true, subProtocol, keepAliveInterval: TimeSpan.FromSeconds(30));
				Emit("client connected + authenticated");
				_activeWebSocket = ws;
				ClientConnected?.Invoke();
				try {
					await MessageLoopAsync(ws, new WebSocketResponder(this, ws), ct).ConfigureAwait(false);
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

	private async Task HandleHttpClientAsync(NetworkStream stream, HttpRequestHead request, CancellationToken ct) {
		if (!IsAuthorized(request.Headers)) {
			Emit("rejected HTTP MCP request: missing/invalid auth token");
			await WebSocketHandshake.WriteStatusAsync(stream, "401 Unauthorized", ct).ConfigureAwait(false);
			return;
		}

		if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) || !IsMcpTarget(request.Target)) {
			await WebSocketHandshake.WriteStatusAsync(stream, "404 Not Found", ct).ConfigureAwait(false);
			return;
		}

		string body = await WebSocketHandshake.ReadBodyAsync(stream, request.Headers, ct).ConfigureAwait(false);
		var responder = new HttpResponder();
		await DispatchAsync(responder, body, ct).ConfigureAwait(false);
		if (responder.ResponseJson is { } response) {
			await WebSocketHandshake.WriteJsonAsync(stream, "200 OK", response, ct).ConfigureAwait(false);
		} else {
			await WebSocketHandshake.WriteStatusAsync(stream, "202 Accepted", ct).ConfigureAwait(false);
		}
	}

	private bool IsAuthorized(IReadOnlyDictionary<string, string> headers) {
		headers.TryGetValue("x-claude-code-ide-authorization", out string? ideToken);
		string? bearer = null;
		if (headers.TryGetValue("authorization", out string? authHeader)
			&& authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
			bearer = authHeader["Bearer ".Length..].Trim();
		}

		return TokenEquals(ideToken, _authToken) || TokenEquals(bearer, _authToken);
	}

	private static bool TokenEquals(string? presented, string expected) =>
		presented is not null && CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(presented), Encoding.ASCII.GetBytes(expected));

	private async Task MessageLoopAsync(WebSocket ws, IMcpResponder responder, CancellationToken ct) {
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
			_ = DispatchAsync(responder, json, ct);
		}
	}

	private async Task DispatchAsync(IMcpResponder responder, string json, CancellationToken ct) {
		string? idForError = null;
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			bool hasId = root.TryGetProperty("id", out var idElement);
			string? idRaw = hasId ? idElement.GetRawText() : null;
			idForError = idRaw;

			Emit($"recv: method={method ?? "(response)"} id={idRaw ?? "-"}");

			if (method is null) {
				return; // a response to one of our (rare) requests; nothing to do
			}

			switch (method) {
				case "initialize":
					await HandleInitializeAsync(responder, root, idRaw, ct).ConfigureAwait(false);
					break;
				case "notifications/initialized":
				case "notifications/cancelled":
					break; // notifications: no reply
				case "ping":
					await responder.SendResultAsync(idRaw, "{}", ct).ConfigureAwait(false);
					break;
				case "tools/list":
					await responder.SendResultAsync(idRaw, _toolsListJson, ct).ConfigureAwait(false);
					break;
				case "tools/call":
					await HandleToolCallAsync(responder, root, idRaw, ct).ConfigureAwait(false);
					break;
				case "prompts/list":
					await responder.SendResultAsync(idRaw, BuildPromptsListJson(), ct).ConfigureAwait(false);
					break;
				case "prompts/get":
					await HandlePromptsGetAsync(responder, root, idRaw, ct).ConfigureAwait(false);
					break;
				default:
					if (idRaw is not null) {
						await responder.SendErrorAsync(idRaw, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
					}

					break;
			}
		} catch (JsonException ex) {
			Emit($"dispatch parse error: {ex.Message}");
			await responder.SendErrorAsync(null, -32700, "Parse error", ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			Emit($"dispatch error: {ex.Message}");
			await responder.SendErrorAsync(idForError, -32603, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private static bool IsMcpTarget(string target) =>
		string.Equals(target, "/mcp", StringComparison.Ordinal) || target.StartsWith("/mcp?", StringComparison.Ordinal);

	// Returns the registry or reports it as unavailable (caught in HandleToolCallAsync). Lets each tool handler
	// open with `var x = Require(_x, "X");` instead of repeating a null-guard + early SendToolError.
	private static T Require<T>(T? registry, string what) where T : class =>
		registry ?? throw new ToolUnavailableException($"{what} is not available.");

	private async Task HandleInitializeAsync(IMcpResponder responder, JsonElement root, string? idRaw, CancellationToken ct) {
		// Echo the client's protocolVersion (echoing is robust to version disagreement).
		string protocolVersion = "2025-03-26";
		if (root.TryGetProperty("params", out var p) &&
			p.TryGetProperty("protocolVersion", out var pv) &&
			pv.ValueKind == JsonValueKind.String) {
			protocolVersion = pv.GetString() ?? protocolVersion;
		}

		string promptsCapability = _prompts.Count > 0 ? ",\"prompts\":{\"listChanged\":false}" : string.Empty;
		string result = "{\"protocolVersion\":" + JsonString(protocolVersion) +
			",\"capabilities\":{\"tools\":{\"listChanged\":false}" + promptsCapability +
			"},\"serverInfo\":{\"name\":\"weavie\",\"version\":\"0.1.0\"}}";
		await responder.SendResultAsync(idRaw, result, ct).ConfigureAwait(false);
	}

	private async Task HandleToolCallAsync(IMcpResponder responder, JsonElement root, string? idRaw, CancellationToken ct) {
		try {
			await DispatchToolAsync(responder, root, idRaw, ct).ConfigureAwait(false);
		} catch (ToolUnavailableException ex) {
			// A handler asked for a registry the IDE-mode server wasn't wired with; report it as a tool error.
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task DispatchToolAsync(IMcpResponder responder, JsonElement root, string? idRaw, CancellationToken ct) {
		if (!root.TryGetProperty("params", out var p) || !p.TryGetProperty("name", out var nameEl)) {
			await responder.SendErrorAsync(idRaw, -32602, "Invalid params", ct).ConfigureAwait(false);
			return;
		}

		string? name = nameEl.GetString();
		p.TryGetProperty("arguments", out var args);
		Emit($"tools/call name={name}");

		switch (name) {
			case "openDiff":
				await HandleOpenDiffAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "openFile":
				string? path = args.TryGetProperty("filePath", out var fp) ? fp.GetString() : null;
				if (!string.IsNullOrEmpty(path)) {
					// `preview` defaults to false (a persistent tab). Coerced leniently: embedded Claude sends
					// scalar args as JSON strings ("true").
					await _presenter.OpenFileAsync(path, ReadLenientBool(args, "preview"), ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(responder, idRaw, "FILE_OPENED", ct).ConfigureAwait(false);
				break;
			case "getWorkspaceFolders":
				await SendToolTextAsync(responder, idRaw, BuildWorkspaceFoldersJson(), ct).ConfigureAwait(false);
				break;
			case "getOpenEditors":
				// JSON-stringified {tabs:[...]} in the text item (claudecode.nvim shape). The page reports the
				// open-tab set via open-editors-changed; empty until it does.
				await SendToolTextAsync(responder, idRaw, BuildOpenEditorsResult(_editor?.OpenEditors, _editor?.Active), ct).ConfigureAwait(false);
				break;
			case "getCurrentSelection":
			case "getLatestSelection":
				// Stringified {success, text, filePath, selection} in the text item (the shape claude parses).
				await SendToolTextAsync(responder, idRaw, BuildSelectionResult(_editor?.Active), ct).ConfigureAwait(false);
				break;
			case "getDiagnostics":
				await responder.SendResultAsync(idRaw, "{\"content\":[{\"type\":\"text\",\"text\":\"[]\"}]}", ct).ConfigureAwait(false);
				break;
			case "close_tab":
				// Resolve the tab name (label or path) against the reported open set, then ask the page to
				// close it. Acknowledge "OK" regardless (an unknown tab is a no-op, not an error).
				string? closeName = args.TryGetProperty("tab_name", out var tnEl) ? tnEl.GetString() : null;
				string? closePath = ResolveTabPath(closeName, _editor?.OpenEditors);
				if (closePath is not null) {
					await _presenter.CloseTabAsync(closePath, ct).ConfigureAwait(false);
				}

				await SendToolTextAsync(responder, idRaw, "OK", ct).ConfigureAwait(false);
				break;
			case "closeAllDiffTabs":
			case "saveDocument":
				await SendToolTextAsync(responder, idRaw, "OK", ct).ConfigureAwait(false);
				break;
			case "currentSession":
				await HandleCurrentSessionAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "listSettings":
				await HandleListSettingsAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "getSetting":
				await HandleGetSettingAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "setSetting":
				await HandleSetSettingAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "clearSetting":
				await HandleClearSettingAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "getLayout":
				await HandleGetLayoutAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "setLayout":
				await HandleSetLayoutAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "listCommands":
				await HandleListCommandsAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "runCommand":
				await HandleRunCommandAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "listThemes":
				await HandleListThemesAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "describeTheme":
				await HandleDescribeThemeAsync(responder, idRaw, ct).ConfigureAwait(false);
				break;
			case "setThemeOverride":
				await HandleSetThemeOverrideAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "applyThemeTransform":
				await HandleApplyThemeTransformAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			case "removeThemeOverride":
				await HandleRemoveThemeOverrideAsync(responder, args, idRaw, ct).ConfigureAwait(false);
				break;
			default:
				await responder.SendErrorAsync(idRaw, -32601, $"Unknown tool: {name}", ct).ConfigureAwait(false);
				break;
		}
	}

	private async Task HandleCurrentSessionAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) {
		var currentSessionId = Require(_currentSessionId, "Current session");
		await SendToolTextAsync(responder, idRaw, $"{{\"id\":{JsonString(currentSessionId())}}}", ct).ConfigureAwait(false);
	}

	private async Task HandleListSettingsAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		// Resolve workspace-scoped keys against this session's workspace, matching setSetting's routing.
		string root = PrimaryWorkspaceRoot;
		string json = root.Length > 0 ? settings.BuildCatalogJson(root) : settings.BuildCatalogJson();
		await SendToolTextAsync(responder, idRaw, json, ct).ConfigureAwait(false);
	}

	private async Task HandleGetSettingAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(responder, idRaw, "getSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			// Resolve a workspace-scoped key against this session's workspace, matching setSetting's routing.
			string root = PrimaryWorkspaceRoot;
			string json = root.Length > 0 ? settings.BuildGetJson(key, root) : settings.BuildGetJson(key);
			await SendToolTextAsync(responder, idRaw, json, ct).ConfigureAwait(false);
		} catch (UnknownSettingException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	// The primary workspace root (empty when none) — where a workspace-scoped write/clear is routed.
	private string PrimaryWorkspaceRoot => _workspaceFolders.Count > 0 ? _workspaceFolders[0] : string.Empty;

	private async Task HandleSetSettingAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(responder, idRaw, "setSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("value", out var valueElement)) {
			await SendToolErrorAsync(responder, idRaw, "setSetting requires a 'value'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			// Route a workspace-scoped key (test.profile, worktree.*) to its out-of-repo overlay via the primary
			// workspace root; SettingsStore ignores the root for user-scoped keys.
			string root = PrimaryWorkspaceRoot;
			var result = root.Length > 0 ? settings.Set(key, valueElement, root) : settings.Set(key, valueElement);
			await SendToolTextAsync(responder, idRaw, FormatSetSummary(key, valueElement, result), ct).ConfigureAwait(false);
			Emit($"setSetting {key} = {valueElement.GetRawText()}");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
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

	private async Task HandleClearSettingAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		var settings = Require(_settings, "Settings");
		string? key = args.GetStringOrNull("key");
		if (string.IsNullOrEmpty(key)) {
			await SendToolErrorAsync(responder, idRaw, "clearSetting requires a 'key'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			// Match setSetting's routing so a workspace override is cleared from its overlay, not the user file.
			string root = PrimaryWorkspaceRoot;
			var result = root.Length > 0 ? settings.Clear(key, root) : settings.Clear(key);
			await SendToolTextAsync(responder, idRaw, FormatClearSummary(key, result), ct).ConfigureAwait(false);
			Emit($"clearSetting {key} (removed={result.Removed})");
		} catch (Exception ex) when (ex is UnknownSettingException or SettingsFileMalformedException) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
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

	private async Task HandleGetLayoutAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) {
		var layout = Require(_layout, "Layout");
		await SendToolTextAsync(responder, idRaw, LayoutSerialization.SerializeCompact(layout.Current), ct).ConfigureAwait(false);
	}

	private async Task HandleSetLayoutAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		var layout = Require(_layout, "Layout");
		if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("root", out var rootElement)) {
			await SendToolErrorAsync(responder, idRaw, "setLayout requires a 'root' layout tree.", ct).ConfigureAwait(false);
			return;
		}

		LayoutNode? root;
		try {
			root = JsonSerializer.Deserialize<LayoutNode>(rootElement.GetRawText(), LayoutSerialization.Options);
		} catch (JsonException ex) {
			await SendToolErrorAsync(responder, idRaw, $"setLayout: invalid root ({ex.Message}).", ct).ConfigureAwait(false);
			return;
		}

		if (root is null) {
			await SendToolErrorAsync(responder, idRaw, "setLayout: 'root' was null.", ct).ConfigureAwait(false);
			return;
		}

		string? focused = args.GetStringOrNull("focused");
		try {
			var result = layout.SetPanes(root, focused, LayoutSource.Mcp);
			await SendToolTextAsync(responder, idRaw, result.Summary, ct).ConfigureAwait(false);
			Emit($"setLayout applied ({result.Summary})");
		} catch (LayoutValidationException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private async Task HandleListCommandsAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) {
		var commands = Require(_commands, "Commands");
		// Prefer the keybinding store's catalog (it includes each command's current keys); fall back to the
		// registry alone (no keys) when no keybinding store was wired.
		string commandsArray = _keybindings is not null
			? _keybindings.BuildCommandsJson()
			: CommandCatalog.BuildCommandsArrayJson(commands.Registry.Definitions, []);
		await SendToolTextAsync(responder, idRaw, $"{{\"commands\":{commandsArray}}}", ct).ConfigureAwait(false);
	}

	private async Task HandleRunCommandAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		var commands = Require(_commands, "Commands");
		bool hasArgs = args.ValueKind == JsonValueKind.Object;
		string? id = args.GetStringOrNull("id");
		if (string.IsNullOrEmpty(id)) {
			await SendToolErrorAsync(responder, idRaw, "runCommand requires an 'id'.", ct).ConfigureAwait(false);
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
				await SendToolTextAsync(responder, idRaw, result.Message ?? $"Ran {id}.", ct).ConfigureAwait(false);
				Emit($"runCommand {id} ok");
			} else {
				await SendToolErrorAsync(responder, idRaw, result.Error ?? $"Command '{id}' failed.", ct).ConfigureAwait(false);
				Emit($"runCommand {id} failed: {result.Error}");
			}
		} catch (UnknownCommandException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
		}
	}

	private static Task SendToolErrorAsync(IMcpResponder responder, string? idRaw, string text, CancellationToken ct) =>
		responder.SendResultAsync(idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}],\"isError\":true}}", ct);

	private async Task HandleOpenDiffAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string? GetArg(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) ? v.GetString() : null;

		string? oldPath = GetArg("old_file_path");
		string? newPath = GetArg("new_file_path") ?? oldPath;
		string newContents = GetArg("new_file_contents") ?? string.Empty;
		string tabName = GetArg("tab_name") ?? "Claude Code";

		if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) {
			await responder.SendErrorAsync(idRaw, -32602, "openDiff requires old_file_path/new_file_path", ct).ConfigureAwait(false);
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
			await SendToolTextsAsync(responder, idRaw, ["FILE_SAVED", outcome.FinalContents ?? newContents], ct).ConfigureAwait(false);
			Emit($"openDiff KEEP -> {newPath} (Claude writes)");
		} else {
			await SendToolTextsAsync(responder, idRaw, ["DIFF_REJECTED", tabName], ct).ConfigureAwait(false);
			Emit("openDiff REJECT");
		}
	}

	private static Task SendToolTextAsync(IMcpResponder responder, string? idRaw, string text, CancellationToken ct) =>
		responder.SendResultAsync(idRaw, $"{{\"content\":[{{\"type\":\"text\",\"text\":{JsonString(text)}}}]}}", ct);

	// Multi-item text content — the MCP shape openDiff expects: [FILE_SAVED, <final contents>] on accept,
	// [DIFF_REJECTED, <tab_name>] on reject (matches coder/claudecode.nvim).
	private static Task SendToolTextsAsync(IMcpResponder responder, string? idRaw, IReadOnlyList<string> texts, CancellationToken ct) {
		string items = string.Join(",", texts.Select(t => $"{{\"type\":\"text\",\"text\":{JsonString(t)}}}"));
		return responder.SendResultAsync(idRaw, $"{{\"content\":[{items}]}}", ct);
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
			writer.WriteString("rootPath", PrimaryWorkspaceRoot);
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
		if (Interlocked.Exchange(ref _disposed, 1) != 0) {
			return;
		}

		_editor?.Changed -= OnActiveEditorChanged;

		if (_cts is not null) {
			await _cts.CancelAsync().ConfigureAwait(false);
			_cts.Dispose();
		}

		_listener?.Stop();
		_sendLock.Dispose();
	}

}
