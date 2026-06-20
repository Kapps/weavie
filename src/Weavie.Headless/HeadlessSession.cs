using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The headless Weavie session: builds the same <c>Weavie.Core</c> graph the native shells do — terminals
/// (claude + shell), the host-backed file:// provider, the IDE-MCP server, layout + editor-session stores,
/// and change tracking — wired to an <see cref="IHostBridge"/> that happens to be a WebSocket rather than a
/// web view. It is the platform-agnostic <c>WorkspaceHost</c> / <c>AppDelegate</c> with the native window,
/// geometry, and main-thread marshaling removed: the bridge's outbound pump is already thread-safe, so off-
/// thread Core events post directly. One session serves the process; a browser reconnect re-sends <c>ready</c>
/// and the session re-pushes its state. See docs/specs/headless-host.md.
/// </summary>
internal sealed class HeadlessSession : IAsyncDisposable {
	private readonly IHostBridge _bridge;
	private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pendingWebCommands = new();

	private SettingsStore? _settings;
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private FileProviderService? _fileProvider;
	private IdeIntegration? _ide;
	private LayoutStore? _layout;
	private EditorStore? _editor;
	private EditorSessionStore? _editorSession;
	private SessionChangeTracker? _changes;
	private LocalFileSystem? _fileSystem;
	private CommandRegistry? _commandRegistry;
	private KeybindingStore? _keybindings;
	private CommandDispatcher? _commands;
	private WorkspaceFileIndex? _fileIndex;
	private WorkspaceBrowser? _browser;
	private ThemeOverridesStore? _themeOverrides;
	private string? _workspace;

	/// <summary>Creates the session over <paramref name="bridge"/>; call <see cref="Start"/> to build the graph.</summary>
	public HeadlessSession(IHostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		_bridge = bridge;
	}

	/// <summary>The absolute workspace root this session serves (resolved in <see cref="Start"/>).</summary>
	public string Workspace => _workspace ?? Environment.CurrentDirectory;

	/// <summary>
	/// Builds the full session graph: settings, commands/keybindings, the two terminal controllers, the
	/// file provider, the IDE-MCP server (injecting its discovery env into the spawned claude), the layout +
	/// editor-session stores, and change tracking. Safe to call once, before any page connects.
	/// </summary>
	public void Start(string workspaceOverride) {
		_layout = LayoutPanes.CreateStore(filePath: null);

		_settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		_settings.Log += Log;

		_commandRegistry = CoreCommands.CreateRegistry();
		_keybindings = new KeybindingStore(_commandRegistry, filePath: null, enableWatcher: true);
		_keybindings.Log += Log;
		_commands = new CommandDispatcher(_commandRegistry);

		_bridge.MessageReceived += OnWebMessage;

		var fileSystem = new LocalFileSystem();
		_fileSystem = fileSystem;
		// The workspace this headless host serves: an explicit override, else the user's configured
		// workspace, else the process working directory (the box the user connects into).
		string workspace = !string.IsNullOrEmpty(workspaceOverride)
			? workspaceOverride
			: _settings.GetString("workspace") ?? Environment.CurrentDirectory;
		_workspace = workspace;

		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings) { Workspace = workspace };
		_claude.Workspace = workspace;

		// Scratch (untitled) buffers live in a per-workspace dir OUTSIDE the workspace, served by the file
		// provider alongside it — keyed off the session root so it tracks the active session's worktree.
		string scratchDir = WeaviePaths.WorkspaceScratchDir(WorkspaceId.ForPath(workspace));
		_fileProvider = new FileProviderService(fileSystem, workspace, scratchDir);
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);

		// Path services for the Windows-style chrome, all rooted at the SESSION root (workspace) so they
		// track the active session's worktree: the omnibar's Go-to-File index and the file browser's tree.
		_fileIndex = new WorkspaceFileIndex(fileSystem, workspace);
		_fileIndex.Log += line => Log($"[index] {line}");
		_browser = new WorkspaceBrowser(fileSystem, workspace);
		// Backs the injected __WEAVIE_THEME__: the active theme id + the user's per-theme override ops.
		_themeOverrides = new ThemeOverridesStore(fileSystem, path: null);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		_editor = new EditorStore();

		// Per-workspace editor session (open files + Monaco view state), keyed by the workspace path id.
		_editorSession = new EditorSessionStore(fileSystem, WeaviePaths.WorkspaceEditorSessionFile(WorkspaceId.ForPath(workspace)));
		_editorSession.Log += line => Log($"[editor-session] {line}");

		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links.
		_changes = new SessionChangeTracker(fileSystem);
		_ide = new IdeIntegration(
			new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout, _editor,
			commands: _commands, keybindings: _keybindings, themeOverrides: _themeOverrides,
			editLocator: _changes.EditLocationFor);
		_ide.Server.Log += line => Log($"[mcp] {line}");
		if (_ide.RegistryServer is { } registryServer) {
			registryServer.Log += line => Log($"[registry] {line}");
		}

		_claude.ExtraEnvironment = _ide.EnvironmentVariables;
		_claude.McpConfigPath = _ide.WriteMcpConfigFile();
		_claude.SettingsFilePath = _ide.WriteSettingsFile();
		_claude.SystemPromptFilePath = _ide.WriteSystemPromptFile();
		_ide.HookBridge.Observed += request => Log($"[hook] {request.Event} {request.ToolName}");
		_ide.HookBridge.Log += line => Log($"[hook] {line}");

		// Session change tracking off the same hook stream (baseline at PreToolUse, content at PostToolUse).
		_ide.HookBridge.Observed += _changes.Observe;
		_changes.Changed += PushChangesToWeb;
		_changes.FileChanged += PushRefreshToWeb;
		_changes.FileChanged += PushTurnDiffToWeb;
		_changes.TurnBegan += PushTurnReset;
		Log($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// A changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		_settings.Subscribe("terminal.shell", _ => _shell?.Restart());

		// Fonts + editor options (ApplyMode.Live): re-push resolved values when a matching setting changes.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}

			if (EditorSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(EditorSettings.BuildJson(_settings, "editorOptions"));
			}
		};

		// Layout: push the canonical document when the store changes (a reconciled web edit or MCP setLayout).
		_layout.Changed += _ => PushLayoutToWeb();

		// Commands: wire the dispatcher to the web and register the Core-side reopen-terminal handler.
		_commands.WebInvoker = InvokeWebCommandAsync;
		_commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			_shell?.Restart();
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});

		// Keybindings (user-edited ~/.weavie/keybindings.json): re-push the catalog + resolved bindings live.
		_keybindings.KeybindingsChanged += () => _bridge.PostToWeb(
			$"{{\"type\":\"commands\",\"commands\":{_keybindings!.BuildCommandsJson()},"
			+ $"\"keybindings\":{_keybindings.BuildKeybindingsJson()}}}");
	}

	/// <summary>
	/// The page-bootstrap script the host injects into <c>index.html</c> before the module graph runs:
	/// advertises the bridge WebSocket (so the web picks the WebSocket transport) and seeds the fonts +
	/// command/keybinding catalog so both surfaces mount correctly with no flash. Mirrors the
	/// <c>AddUserScript</c> document-start injections the native shells do.
	/// </summary>
	public string BuildBootstrapScript() {
		string fonts = _settings is null ? "undefined" : FontSettings.BuildJson(_settings, messageType: null);
		// Resolved editor options (Monaco IEditorOptions) — the editor reads __WEAVIE_EDITOR_OPTIONS__
		// synchronously at creation and throws if it's absent, so the headless host injects it like the
		// native shells do (Win's WorkspaceWindow / Mac's AppDelegate).
		string editorOptions = _settings is null ? "undefined" : EditorSettings.BuildJson(_settings, messageType: null);
		string commands = _keybindings is null ? "[]" : _keybindings.BuildCommandsJson();
		string keybindings = _keybindings is null ? "[]" : _keybindings.BuildKeybindingsJson();
		// Active theme + per-theme overrides, so the chrome/editor/terminal mount themed (no default-asset
		// fetch, no flash) — the native shells inject the same __WEAVIE_THEME__ before navigation.
		string theme = _settings is null || _themeOverrides is null
			? "undefined"
			: ThemeJson.Build(_settings, _themeOverrides, messageType: null, log: Log);
		// A browser has no native window chrome, so — like the Windows shell — render Weavie's custom title
		// bar (icon + File/View menus + Omnibar + window controls). The window controls are cosmetic here
		// (no OS window to drive); the value is the Omnibar Go-to-File and the menus.
		string workspaceLabel = string.IsNullOrEmpty(_workspace) ? "weavie" : new DirectoryInfo(_workspace).Name;
		string shell = ShellProtocol.BuildConfigScript("web", "custom", workspaceLabel, []);
		// File-browser discovery: only the session root is needed in Phase 1. The LSP bridge isn't tunneled
		// yet, so the server list is empty and the editor's language client never tries to connect. Rooted at
		// the session root (_workspace) so the browser tracks the active session's worktree.
		string lsp = JsonSerializer.Serialize(new {
			url = string.Empty,
			token = string.Empty,
			workspace = _workspace ?? string.Empty,
			servers = Array.Empty<object>(),
		});
		return
			"window.__WEAVIE_BRIDGE_WS__ = \"auto\";"
			+ $"window.__WEAVIE_FONTS__ = {fonts};"
			+ $"window.__WEAVIE_EDITOR_OPTIONS__ = {editorOptions};"
			+ $"window.__WEAVIE_THEME__ = {theme};"
			+ $"window.__WEAVIE_LSP__ = {lsp};"
			+ $"window.__WEAVIE_COMMANDS__ = {commands};"
			+ $"window.__WEAVIE_KEYBINDINGS__ = {keybindings};"
			+ shell;
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		foreach (var pending in _pendingWebCommands.Values) {
			pending.TrySetResult(CommandResult.Failure("The host is shutting down before the command completed."));
		}

		_claude?.Dispose();
		_shell?.Dispose();
		if (_ide is not null) {
			await _ide.DisposeAsync().ConfigureAwait(false);
		}

		_keybindings?.Dispose();
		_settings?.Dispose();
	}

	private void OnWebMessage(string json) {
		string type;
		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Log($"[weavie] (unparsed) {json}");
			return;
		}

		switch (type) {
			case "term-input":
				byte[] input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
				TerminalFor(root)?.Write(input);
				break;
			case "term-resize":
				TerminalFor(root)?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "term-ready":
				TerminalFor(root)?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "diff-resolved":
				string diffId = root.GetProperty("id").GetString() ?? string.Empty;
				bool kept = root.TryGetProperty("kept", out var keptEl) && keptEl.GetBoolean();
				string? finalContents = root.TryGetProperty("finalContents", out var fcEl) ? fcEl.GetString() : null;
				_diffPresenter?.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				int revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				_fileOpener?.Open(revealPath, revealLine, preview: false, scratch: false);
				break;
			case "request-file-index":
				if (_fileIndex is not null) {
					_bridge.PostToWeb(ShellProtocol.BuildFileIndex(_fileIndex.Root, _fileIndex.List(WorkspaceFileIndex.DefaultCap)));
				}

				break;
			case "list-dir":
				ListDirectory(root.TryGetProperty("path", out var ldEl) ? ldEl.GetString() ?? string.Empty : string.Empty);
				break;
			// Custom title bar in a browser: there's no OS window to minimize/maximize/close or resize, and the
			// session's workspace is fixed, so these chrome messages are cosmetic no-ops here (logged, not acted on).
			case "window-control":
			case "window-resize":
			case "menu-action":
				break;
			case "active-editor-changed":
				if (_editor is not null && ActiveEditor.TryParse(root, out var activeEditor) && activeEditor is not null) {
					_editor.SetActive(activeEditor);
				}

				break;
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "fs-stat":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Stat(FsId(root), FsPath(root)));
				}

				break;
			case "fs-read":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Read(FsId(root), FsPath(root)));
				}

				break;
			case "fs-write":
				if (_fileProvider is not null) {
					_bridge.PostToWeb(_fileProvider.Write(
						FsId(root), FsPath(root),
						root.TryGetProperty("content", out var fsContentEl) ? fsContentEl.GetString() ?? string.Empty : string.Empty));
				}

				break;
			case "accept-turn":
				AcceptTurn();
				break;
			case "undo-turn":
				UndoTurn();
				break;
			case "invoke-command":
				InvokeCommandFromWeb(
					root.TryGetProperty("id", out var ciEl) ? ciEl.GetString() ?? string.Empty : string.Empty,
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null);
				break;
			case "command-ack":
				CompleteWebCommand(root);
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout + editor session so both
				// restore on connect (launch / browser reload).
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				Log($"[weavie] {json}");
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			case "editor-session-changed":
				HandleEditorSessionChanged(root);
				break;
			default:
				Log($"[weavie] {json}");
				break;
		}
	}

	private void HandleLayoutChanged(JsonElement root) {
		if (_layout is null || !root.TryGetProperty("document", out var documentElement)) {
			return;
		}

		if (!LayoutSerialization.TryDeserialize(documentElement.GetRawText(), out var document, out string? error)
			|| document is null) {
			Log($"[weavie] layout-changed: bad document ({error})");
			return;
		}

		try {
			_layout.SetPanes(document.Root, document.Focused, LayoutSource.User);
		} catch (LayoutValidationException ex) {
			Log($"[weavie] layout-changed rejected: {ex.Message}");
		}
	}

	private void PushLayoutToWeb() {
		if (_layout is null) {
			return;
		}

		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Lists <paramref name="requestedPath"/> under the session root and pushes a <c>dir-listing</c>
	/// reply (directories first) for the file browser; an empty path means the workspace root.</summary>
	private void ListDirectory(string requestedPath) {
		if (_browser is null) {
			return;
		}

		var entries = _browser.List(requestedPath);
		string json = JsonSerializer.Serialize(new {
			type = "dir-listing",
			path = string.IsNullOrEmpty(requestedPath) ? _browser.Root : requestedPath,
			entries = entries.Select(e => new { name = e.Name, path = e.Path, isDir = e.IsDirectory }),
		});
		_bridge.PostToWeb(json);
	}

	private void HandleEditorSessionChanged(JsonElement root) {
		if (_editorSession is null || !root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Log($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		_editorSession.Update(session);
	}

	private void PushEditorSessionToWeb() => _bridge.PostToWeb(_editorSession?.BuildRestoreJson()
		?? "{\"type\":\"set-editor-session\",\"session\":{\"active\":null,\"open\":[]}}");

	private void PushChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_changes));
		}
	}

	private void PushChangeDiffToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	private void PushRefreshToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	private void PushTurnDiffToWeb(string path) {
		if (_changes?.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	private void PushTurnReset() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	private void AcceptTurn() {
		if (_changes is null) {
			return;
		}

		_changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
	}

	private void UndoTurn() {
		if (_changes is null || _fileSystem is null || _workspace is null) {
			return;
		}

		var truncated = new List<string>();
		foreach (var change in _changes.TurnChanges()) {
			try {
				if (!BufferStore.Save(_fileSystem, _workspace, change.Path, change.BaselineText)) {
					Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: path is outside the workspace.");
					continue;
				}

				if (change.BaselineText.Length == 0) {
					truncated.Add(Path.GetFileName(change.Path));
				}

				_changes.RecordChange(change.Path);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: {ex.Message}");
			}
		}

		if (truncated.Count > 0) {
			Notify("warn", $"Undo emptied {truncated.Count} file(s) created this turn (delete manually): {string.Join(", ", truncated)}");
		}
	}

	private void Notify(string level, string message) =>
		_bridge.PostToWeb($"{{\"type\":\"notify\",\"level\":{JsonString(level)},\"message\":{JsonString(message)}}}");

	private void InvokeCommandFromWeb(string id, string? argsJson) {
		if (_commands is null || string.IsNullOrEmpty(id)) {
			return;
		}

		_ = RunCommandSafeAsync(id, argsJson);
	}

	private async Task RunCommandSafeAsync(string id, string? argsJson) {
		if (_commands is null) {
			return;
		}

		try {
			var result = await _commands.InvokeAsync(id, argsJson, CancellationToken.None).ConfigureAwait(false);
			if (!result.Ok) {
				Log($"[weavie] invoke-command {id} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Log($"[weavie] invoke-command {id} error: {ex.Message}");
		}
	}

	private async Task<CommandResult> InvokeWebCommandAsync(string id, string? argsJson, CancellationToken ct) {
		string token = Guid.NewGuid().ToString("n");
		var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pendingWebCommands[token] = completion;
		try {
			string argsPart = string.IsNullOrEmpty(argsJson) ? "null" : argsJson;
			_bridge.PostToWeb(
				$"{{\"type\":\"run-command\",\"id\":{JsonString(id)},\"args\":{argsPart},\"token\":{JsonString(token)}}}");

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(TimeSpan.FromSeconds(5));
			await using (timeout.Token.Register(() => completion.TrySetResult(
				CommandResult.Failure($"Command '{id}' was dispatched but the UI didn't acknowledge within 5s."))).ConfigureAwait(false)) {
				return await completion.Task.ConfigureAwait(false);
			}
		} finally {
			_pendingWebCommands.TryRemove(token, out _);
		}
	}

	private void CompleteWebCommand(JsonElement root) {
		string? token = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
		if (string.IsNullOrEmpty(token) || !_pendingWebCommands.TryGetValue(token, out var completion)) {
			return;
		}

		bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False && okEl.GetBoolean();
		string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
		completion.TrySetResult(ok ? CommandResult.Success() : CommandResult.Failure(error ?? "The command failed in the UI."));
	}

	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
	}

	private static string FsId(JsonElement root) => root.TryGetProperty("id", out var e) ? e.GetString() ?? string.Empty : string.Empty;

	private static string FsPath(JsonElement root) => root.TryGetProperty("path", out var e) ? e.GetString() ?? string.Empty : string.Empty;

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
