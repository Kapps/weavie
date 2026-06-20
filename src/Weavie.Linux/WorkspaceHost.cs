using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Linux;

/// <summary>
/// The GTK + WebKitGTK application host: builds the web-view window, wires the JS bridge, terminals,
/// file opener, and MCP diff presenter, and starts the IDE-MCP server so the spawned claude connects
/// back as the sole edit feed. The Linux counterpart of the macOS <c>AppDelegate</c>; all logic below
/// the native window/view is shared <c>Weavie.Core</c>.
/// </summary>
internal sealed class WorkspaceHost {
	private readonly HostBridge _bridge = new();
	// In-flight web commands invoked by Claude (runCommand → run-command): token → completion, settled by
	// the web's command-ack (or a 5s timeout).
	private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pendingWebCommands = new();

	private AppSchemeHandler? _scheme;
	private SettingsStore? _settings;
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private FileProviderService? _fileProvider;
	private IdeIntegration? _ide;
	private LayoutStore? _layout;
	private EditorStore? _editor;
	private SessionChangeTracker? _changes;
	private LocalFileSystem? _fileSystem;
	private string? _workspace;
	private CommandRegistry? _commandRegistry;
	private KeybindingStore? _keybindings;
	private CommandDispatcher? _commands;

	private IntPtr _window;
	private IntPtr _webView;
	private IntPtr _contentManager;
	// Kept alive for the lifetime of the host: native holds a bare function pointer to this.
	private WidgetCallback? _onDestroy;

	/// <summary>
	/// Builds the window and WebKit view, registers the <c>app://</c> scheme handler and the
	/// <c>weavie</c> script-message bridge, starts the terminals and IDE-MCP server (injecting its
	/// discovery env into the spawned claude), restores the saved geometry, and loads the web app.
	/// Must be called on the GTK main thread (after <c>gtk_init</c>, before <c>gtk_main</c>).
	/// </summary>
	internal void Start() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

		// Restore the saved window geometry when present and sane, else a 1280x840 default.
		_layout = LayoutPanes.CreateStore(filePath: null);
		var savedWindow = _layout.Current.Window;
		bool usedSaved = savedWindow is { Width: > 0, Height: > 0 };
		int width = usedSaved ? savedWindow!.Width : 1280;
		int height = usedSaved ? savedWindow!.Height : 840;

		// Custom app:// scheme on the default web context (the one the view below uses), the script-message
		// bridge on a fresh user-content manager, then the view bound to that manager.
		_scheme = new AppSchemeHandler(wwwroot);
		_scheme.Register(WebKit.webkit_web_context_get_default());
		_contentManager = WebKit.webkit_user_content_manager_new();
		_bridge.RegisterOn(_contentManager);
		_webView = WebKit.webkit_web_view_new_with_user_content_manager(_contentManager);
		_bridge.Attach(_webView);
		WebKit.webkit_settings_set_enable_developer_extras(WebKit.webkit_web_view_get_settings(_webView), true);

		// User settings (shell / workspace / claude path) resolved from ~/.weavie/settings.toml; the
		// store is the change hub the host reacts to (e.g. a shell change reopens the shell pane).
		_settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		_settings.Log += Log;

		// Commands + keybindings: the app's command catalog (CoreCommands) and the user keybindings resolved
		// from ~/.weavie/keybindings.json over the defaults. The dispatcher routes runCommand (MCP) +
		// invoke-command (web) to Core/web handlers; the WebInvoker + Core handlers are wired below.
		_commandRegistry = CoreCommands.CreateRegistry();
		_keybindings = new KeybindingStore(_commandRegistry, filePath: null, enableWatcher: true);
		_keybindings.Log += Log;
		_commands = new CommandDispatcher(_commandRegistry);

		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the
		// discovery env so the spawned claude connects to us (the SOLE edit feed). The same store backs
		// the settings MCP tools, so the user can change settings by talking to claude.
		var fileSystem = new LocalFileSystem();
		_fileSystem = fileSystem;
		string workspace = _settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_workspace = workspace;
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		// Scratch (untitled) buffers live in a per-workspace dir OUTSIDE the workspace, so they never reach
		// the file tree, git, or Claude. The file provider serves both the workspace and this dir.
		string scratchDir = WeaviePaths.WorkspaceScratchDir(WorkspaceId.ForPath(workspace));
		// Host-backed file:// provider: the editor's working copies read/write the real disk through the
		// host (fs-stat/fs-read/fs-write), scoped to the workspace. Same Core service the Windows/macOS
		// hosts use — without it the editor's file reads time out and nothing opens.
		_fileProvider = new FileProviderService(fileSystem, workspace, scratchDir);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the IDE-MCP server can tell
		// the spawned claude what the user is looking at.
		_editor = new EditorStore();
		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links
		// (the bridge decision runs after the tracker has folded in the PostToolUse content).
		_changes = new SessionChangeTracker(fileSystem);
		_ide = new IdeIntegration(
			new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout, _editor,
			commands: _commands, keybindings: _keybindings, themeOverrides: null, editLocator: _changes.EditLocationFor);
		_ide.Server.Log += line => Log($"[mcp] {line}");
		if (_ide.RegistryServer is { } registryServer) {
			registryServer.Log += line => Log($"[registry] {line}");
		}

		_claude.ExtraEnvironment = _ide.EnvironmentVariables;
		// Capability registry: hand the spawned claude an --mcp-config pointing at the registry server
		// so the settings tools reach the model as mcp__weavie__* (the IDE server's tools are filtered).
		_claude.McpConfigPath = _ide.WriteMcpConfigFile();
		// Hook bridge: a --settings file whose hooks route claude's tool calls to our relay; the observed
		// stream is logged here (the session change view consumes the same feed).
		_claude.SettingsFilePath = _ide.WriteSettingsFile();
		_ide.HookBridge.Observed += request => Log($"[hook] {request.Event} {request.ToolName}");
		_ide.HookBridge.Log += line => Log($"[hook] {line}");

		// Session change tracking: the same hook stream feeds the tracker (baseline at PreToolUse, new
		// content at PostToolUse), independent of openDiff and permission mode. Events arrive off the main
		// thread; marshal before touching the web. (The tracker itself is built above, before the IDE
		// server, so EditLocationFor can back the hook bridge's edit jump-links.)
		_ide.HookBridge.Observed += _changes.Observe;
		_changes.Changed += () => GtkMain.Invoke(PushChangesToWeb);
		_changes.FileChanged += path => GtkMain.Invoke(() => PushRefreshToWeb(path));
		// Inline diff: per-turn diff per edited file + clear-all on a turn boundary (implicit accept).
		_changes.FileChanged += path => GtkMain.Invoke(() => PushTurnDiffToWeb(path));
		_changes.TurnBegan += () => GtkMain.Invoke(PushTurnReset);
		// Review navigator: the per-turn change list, refreshed on change + cleared on a new turn.
		_changes.Changed += () => GtkMain.Invoke(PushTurnChangesToWeb);
		_changes.TurnBegan += () => GtkMain.Invoke(PushTurnChangesToWeb);
		Log($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		_settings.Subscribe("terminal.shell", _ => GtkMain.Invoke(() => _shell?.Restart()));

		// Fonts (ApplyMode.Live): any global or per-surface font change re-pushes the resolved editor +
		// terminal fonts to the web app, which applies them in place. PostToWeb marshals to the main
		// thread itself and the store is thread-safe, so the off-thread change event can call it directly.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}
		};

		// Layout: push the canonical document to the web when the store changes (a reconciled web edit or
		// a future MCP setLayout). Change events arrive off the main thread.
		_layout.Changed += _ => GtkMain.Invoke(PushLayoutToWeb);

		// Commands: wire the dispatcher to the web (Claude's runCommand of a web command posts run-command +
		// awaits its ack) and register the Core-side handlers (reopen terminal → restart the shell pane,
		// marshaled to the main thread).
		_commands.WebInvoker = InvokeWebCommandAsync;
		_commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			GtkMain.Invoke(() => _shell?.Restart());
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});

		// Keybindings (user-edited ~/.weavie/keybindings.json): re-push the catalog + resolved bindings so the
		// web rebuilds its resolver + palette live. PostToWeb marshals to the main thread itself.
		_keybindings.KeybindingsChanged += () => _bridge.PostToWeb(
			$"{{\"type\":\"commands\",\"commands\":{_keybindings.BuildCommandsJson()},"
			+ $"\"keybindings\":{_keybindings.BuildKeybindingsJson()}}}");

		// Typography + command/keybinding catalog: inject at document start so both surfaces mount at the
		// user's font (no default-font flash) and the keybinding resolver / palette are populated at mount.
		// Live changes are pushed by the subscriptions above.
		InjectAtDocumentStart($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings, messageType: null)};");
		InjectAtDocumentStart(
			$"window.__WEAVIE_COMMANDS__ = {_keybindings.BuildCommandsJson()}; "
			+ $"window.__WEAVIE_KEYBINDINGS__ = {_keybindings.BuildKeybindingsJson()};");

		// Window: a single top-level holding the web view, restored to the saved geometry.
		_window = Gtk.gtk_window_new(Gtk.WindowToplevel);
		Gtk.gtk_window_set_title(_window, "weavie");
		Gtk.gtk_window_set_default_size(_window, width, height);
		Gtk.gtk_container_add(_window, _webView);
		if (usedSaved) {
			Gtk.gtk_window_move(_window, savedWindow!.X, savedWindow.Y);
			if (savedWindow.Maximized) {
				Gtk.gtk_window_maximize(_window);
			}
		}

		_onDestroy = OnWindowDestroy;
		_ = GLib.g_signal_connect_data(
			_window, "destroy", Marshal.GetFunctionPointerForDelegate(_onDestroy), IntPtr.Zero, IntPtr.Zero, 0);

		Gtk.gtk_widget_show_all(_window);
		WebKit.webkit_web_view_load_uri(_webView, "app://app/index.html");
	}

	/// <summary>Disposes both terminals and shuts down the IDE-MCP server; called after the main loop exits.</summary>
	internal void Shutdown() {
		SaveWindowState();
		// Fail any web command still awaiting an ack so a runCommand in flight at exit doesn't hang.
		foreach (var pending in _pendingWebCommands.Values) {
			pending.TrySetResult(CommandResult.Failure("The app is terminating before the command completed."));
		}

		_claude?.Dispose();
		_shell?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_keybindings?.Dispose();
		_settings?.Dispose();
	}

	private void OnWindowDestroy(IntPtr widget, IntPtr userData) {
		SaveWindowState();
		Gtk.gtk_main_quit();
	}

	private void InjectAtDocumentStart(string source) {
		IntPtr script = WebKit.webkit_user_script_new(
			source, WebKit.InjectTopFrame, WebKit.InjectAtDocumentStart, IntPtr.Zero, IntPtr.Zero);
		WebKit.webkit_user_content_manager_add_script(_contentManager, script);
	}

	private void SaveWindowState() {
		if (_window == IntPtr.Zero || _layout is null) {
			return;
		}

		_layout.SetWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-maximized restore bounds while maximized.</summary>
	private LayoutGeometry CaptureWindowState() {
		if (Gtk.gtk_window_is_maximized(_window) && _layout!.Current.Window is { } prior) {
			return prior with { Maximized = true };
		}

		Gtk.gtk_window_get_size(_window, out int width, out int height);
		Gtk.gtk_window_get_position(_window, out int x, out int y);
		return new LayoutGeometry {
			X = x,
			Y = y,
			Width = width,
			Height = height,
			Maximized = false,
		};
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
				bool revealPreview = root.TryGetProperty("preview", out var pvEl)
					&& pvEl.ValueKind is JsonValueKind.True or JsonValueKind.False && pvEl.GetBoolean();
				_fileOpener?.Open(revealPath, revealLine, preview: revealPreview, scratch: false);
				break;
			case "active-editor-changed":
				if (_editor is not null && ActiveEditor.TryParse(root, out var activeEditor) && activeEditor is not null) {
					_editor.SetActive(activeEditor);
				}

				break;
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "get-turn-diff":
				PushTurnDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
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
			case "save-buffer":
				SaveBuffer(
					root.GetProperty("path").GetString() ?? string.Empty,
					root.TryGetProperty("content", out var bufEl) ? bufEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "accept-turn":
				AcceptTurn();
				break;
			case "undo-turn":
				UndoTurn();
				break;
			case "invoke-command":
				// A keybinding/palette in the web invoked a Core command — run it here (fire-and-forget).
				InvokeCommandFromWeb(
					root.TryGetProperty("id", out var ciEl) ? ciEl.GetString() ?? string.Empty : string.Empty,
					root.TryGetProperty("args", out var caEl) && caEl.ValueKind == JsonValueKind.Object ? caEl.GetRawText() : null);
				break;
			case "command-ack":
				// The web finished a run-command (a web command Claude invoked over MCP) — settle the await.
				CompleteWebCommand(root);
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout so it restores on launch.
				PushLayoutToWeb();
				Log($"[weavie] {json}");
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			default:
				// log — surface for diagnostics.
				Log($"[weavie] {json}");
				break;
		}
	}

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
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

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		if (_layout is null) {
			return;
		}

		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_changes));
		}
	}

	/// <summary>Pushes the per-turn change list (files changed this turn + each file's first-change line) for the review navigator.</summary>
	private void PushTurnChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.TurnChanges(_changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>
	/// Pushes a targeted live-refresh of one edited file: an <c>fs-change</c> so the page reloads the
	/// non-dirty model from disk (matching the macOS host's file-provider-driven reload).
	/// </summary>
	private void PushRefreshToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>Pushes one file's per-turn diff so the page renders it inline in the live editor.</summary>
	private void PushTurnDiffToWeb(string path) {
		if (_changes?.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>Clears all inline turn markers on a turn boundary (the prior turn is implicitly accepted).</summary>
	private void PushTurnReset() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	/// <summary>Accepts the whole turn's changes: resets the per-turn baseline and clears the page's inline markers.</summary>
	private void AcceptTurn() {
		if (_changes is null) {
			return;
		}

		_changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
	}

	/// <summary>
	/// Undoes the whole turn's changes: reverts every file touched this turn to its turn baseline on disk and
	/// live-refreshes the editor. Files created this turn truncate to empty (not deleted) — surfaced via a toast.
	/// </summary>
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

	/// <summary>
	/// Autosave write: persists the editor buffer for <paramref name="path"/> to disk (constrained to the
	/// workspace) so the embedded claude sees the user's current state. A write that genuinely fails (or a
	/// path outside the workspace, which is a bug in the page) surfaces to the user as an error toast — an
	/// autosave must never silently lose the user's work.
	/// </summary>
	private void SaveBuffer(string path, string content) {
		// The session is wired in Start, before the page can post any message — so a null here is a broken
		// invariant, not a runtime condition to absorb. Fail loud rather than drop the save.
		if (_fileSystem is null || _workspace is null) {
			throw new InvalidOperationException("save-buffer arrived before the session was initialized.");
		}

		try {
			if (!BufferStore.Save(_fileSystem, _workspace, path, content)) {
				Notify("error", $"Couldn't save {Path.GetFileName(path)}: path is outside the workspace.");
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		_bridge.PostToWeb($"{{\"type\":\"notify\",\"level\":{JsonString(level)},\"message\":{JsonString(message)}}}");

	/// <summary>
	/// Runs a Core command the web asked for (its keybinding/palette resolved to a Core command).
	/// Fire-and-forget: the web doesn't await a result for its own triggers; failures are logged.
	/// </summary>
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

	/// <summary>
	/// The dispatcher's web invoker: posts a <c>run-command</c> to the page and awaits its <c>command-ack</c>
	/// (or a 5s timeout). How Claude's <c>runCommand</c> of a web command reaches the UI and gets a result back.
	/// </summary>
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

	/// <summary>Settles the pending web-command await for a <c>command-ack</c> message (by token).</summary>
	private void CompleteWebCommand(JsonElement root) {
		string? token = root.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
		if (string.IsNullOrEmpty(token) || !_pendingWebCommands.TryGetValue(token, out var completion)) {
			return;
		}

		bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False && okEl.GetBoolean();
		string? error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
		completion.TrySetResult(ok ? CommandResult.Success() : CommandResult.Failure(error ?? "The command failed in the UI."));
	}

	/// <summary>The correlation <c>id</c> of an fs-stat/read/write request.</summary>
	private static string FsId(JsonElement root) =>
		root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>The native <c>path</c> of an fs-stat/read/write request.</summary>
	private static string FsPath(JsonElement root) =>
		root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
