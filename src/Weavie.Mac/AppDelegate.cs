using System.Collections.Concurrent;
using System.Text.Json;
using CoreGraphics;
using Foundation;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Mac.Hosting;
using WebKit;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Mac;

/// <summary>
/// The macOS application delegate: builds the WKWebView host window, wires the JS bridge,
/// terminal, file opener, and MCP diff presenter, and starts the IDE-MCP server so the spawned
/// claude connects back as the sole edit feed.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate {
	private readonly HostBridge _bridge = new();
	private SettingsStore? _settings;
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private IdeIntegration? _ide;
	private LayoutStore? _layout;
	private ThemeOverridesStore? _themeOverrides;
	private EditorStore? _editor;
	private EditorSessionStore? _editorSession;
	private SessionChangeTracker? _changes;
	private LocalFileSystem? _fileSystem;
	private FileProviderService? _fileProvider;
	private ScratchStore? _scratch;
	private string? _workspace;
	private NSWindow? _window;
	private WKWebView? _webView;
	private CommandRegistry? _commandRegistry;
	private KeybindingStore? _keybindings;
	private CommandDispatcher? _commands;
	private GlobalHotkeyService? _hotkeyService;
	// In-flight web commands invoked by Claude (runCommand → run-command): token → completion, settled by
	// the web's command-ack (or a 5s timeout).
	private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pendingWebCommands = new();

	/// <summary>
	/// Creates the host window and WKWebView, registers the <c>app://</c> scheme handler and
	/// <c>weavie</c> script-message bridge, starts the terminal and IDE-MCP server (injecting its
	/// discovery env into the spawned claude), and loads the web app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
		// Allow the Web Inspector for local debugging of the prototype.
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));

		// Restore the saved window geometry (size / position / zoomed) when present and on-screen, else
		// fall back to a centered 1280x840 default.
		_layout = LayoutPanes.CreateStore();
		var savedWindow = _layout.Current.Window;
		bool usedSaved = savedWindow is not null
			&& IsOnScreen(new CGRect(savedWindow.X, savedWindow.Y, savedWindow.Width, savedWindow.Height));
		var frame = usedSaved
			? new CGRect(savedWindow!.X, savedWindow.Y, savedWindow.Width, savedWindow.Height)
			: new CGRect(0, 0, 1280, 840);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);

		// User settings (shell / workspace / claude path) resolved from ~/.weavie/settings.toml; the
		// store is the change hub the host reacts to (e.g. a shell change reopens the shell pane).
		_settings = CoreSettings.CreateStore();
		_settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Commands + keybindings: the app's command catalog (CoreCommands) and the user keybindings resolved
		// from ~/.weavie/keybindings.json over the defaults. The dispatcher routes runCommand (MCP) +
		// invoke-command (web) to Core/web handlers; the WebInvoker + Core handlers are wired below.
		_commandRegistry = CoreCommands.CreateRegistry();
		_keybindings = new KeybindingStore(_commandRegistry);
		_keybindings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_commands = new CommandDispatcher(_commandRegistry);

		// Per-theme color overrides (~/.weavie/theme-overrides.json); the active theme itself is a setting.
		_themeOverrides = new ThemeOverridesStore(new LocalFileSystem());
		_themeOverrides.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Typography: inject the resolved editor + terminal fonts at document start so both surfaces
		// mount at the user's font with no default-font flash; live changes are pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		// Theme: inject the active theme (id + override ops, plus the converted JSON for installed themes)
		// at document start so the editor / terminal / chrome mount themed with no flash; live changes pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_THEME__ = {ThemeJson.Build(_settings, _themeOverrides, log: line => Console.WriteLine(line))};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		// Commands + keybindings: inject the catalog + resolved bindings at document start so the web's
		// keybinding resolver and command palette are populated at mount; live edits are pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString(
				$"window.__WEAVIE_COMMANDS__ = {_keybindings.BuildCommandsJson()}; "
				+ $"window.__WEAVIE_KEYBINDINGS__ = {_keybindings.BuildKeybindingsJson()};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject
		// the discovery env so the spawned claude connects to us (the SOLE edit feed). The same store
		// backs the settings MCP tools, so the user can change settings by talking to claude.
		var fileSystem = new LocalFileSystem();
		_fileSystem = fileSystem;
		string workspace = _settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_workspace = workspace;
		var workspaceId = WorkspaceId.ForPath(workspace);
		// Scratch (untitled) buffers live in a per-workspace dir OUTSIDE the workspace, so they never reach the
		// file tree / index / git / Claude; the file provider is told it as a second allowed root.
		string scratchDir = WeaviePaths.WorkspaceScratchDir(workspaceId);
		_scratch = new ScratchStore(fileSystem, scratchDir);
		_fileProvider = new FileProviderService(fileSystem, workspace, scratchDir);
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;

		// Per-workspace editor session: the open files + per-file Monaco view state under
		// ~/.weavie/workspaces/<id>/editor-session.json, so the editor reopens its files at the same
		// scroll/cursor on launch. Web-written; pushed back on ready. Keyed by the workspace path's id so it
		// is per-folder even though the macOS layout store is still the legacy single-window file.
		_editorSession = new EditorSessionStore(fileSystem, WeaviePaths.WorkspaceEditorSessionFile(workspaceId));
		_editorSession.Log += line => {
			Console.WriteLine($"[weavie] {line}");
			Console.Out.Flush();
		};
		// Garbage-collect scratch (untitled) temp files orphaned by a crash or a reset session — keep only those
		// still referenced by the restored editor session (they reopen as their "Untitled-N" tabs).
		_scratch.GarbageCollect(_editorSession.Current.Open.Where(entry => entry.Scratch).Select(entry => entry.Path));
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the IDE-MCP server can tell
		// the spawned claude what the user is looking at.
		_editor = new EditorStore();
		_ide = new IdeIntegration(
			new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout, _editor,
			commands: _commands, keybindings: _keybindings, themeOverrides: _themeOverrides);
		_ide.Server.Log += line => {
			Console.WriteLine($"[mcp] {line}");
			Console.Out.Flush();
		};
		if (_ide.RegistryServer is not null) {
			_ide.RegistryServer.Log += line => {
				Console.WriteLine($"[registry] {line}");
				Console.Out.Flush();
			};
		}

		_claude.ExtraEnvironment = _ide.EnvironmentVariables;
		// Capability registry: hand the spawned claude an --mcp-config pointing at the registry server
		// so the settings tools reach the model as mcp__weavie__* (the IDE server's tools are filtered).
		_claude.McpConfigPath = _ide.WriteMcpConfigFile();
		// Hook bridge: a --settings file whose hooks route claude's tool calls to our relay; the observed
		// stream is logged here (the session change view will consume the same feed).
		_claude.SettingsFilePath = _ide.WriteSettingsFile();
		// Orientation: an --append-system-prompt-file telling claude it's embedded in Weavie and to read
		// live app state (themes/settings/layout) through the mcp__weavie__* tools, not the on-disk config.
		_claude.SystemPromptFilePath = _ide.WriteSystemPromptFile();
		_ide.HookBridge.Observed += request => {
			Console.WriteLine($"[hook] {request.Event} {request.ToolName}");
			Console.Out.Flush();
		};
		_ide.HookBridge.Log += line => {
			Console.WriteLine($"[hook] {line}");
			Console.Out.Flush();
		};

		// Session change tracking: the same hook stream feeds the tracker (baseline at PreToolUse, new
		// content at PostToolUse), independent of openDiff and permission mode. Changed pushes the change
		// list; FileChanged pushes a targeted live-refresh of the one edited file. Events arrive off the
		// main thread; marshal before touching the web.
		_changes = new SessionChangeTracker(fileSystem);
		_ide.HookBridge.Observed += _changes.Observe;
		_changes.Changed += () => InvokeOnMainThread(PushChangesToWeb);
		_changes.FileChanged += path => InvokeOnMainThread(() => PushRefreshToWeb(path));
		// Inline diff: per-turn diff per edited file + clear-all on a turn boundary (implicit accept).
		_changes.FileChanged += path => InvokeOnMainThread(() => PushTurnDiffToWeb(path));
		_changes.TurnBegan += () => InvokeOnMainThread(PushTurnReset);
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the main thread, so marshal onto it before touching the controller.
		_settings.Subscribe("terminal.shell", _ => InvokeOnMainThread(() => _shell?.Restart()));

		// Fonts (ApplyMode.Live): any global or per-surface font change re-pushes the resolved editor +
		// terminal fonts to the web app, which applies them in place. PostToWeb marshals to the main
		// thread itself and the store is thread-safe, so the off-thread change event can call it directly.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}
		};

		// Theme (ApplyMode.Live): a theme switch (theme.active) or an override edit re-pushes the resolved
		// active theme so the web re-themes the editor, terminal, and chrome in place. PostToWeb marshals to
		// the main thread and the stores are thread-safe, so the off-thread events can call it directly.
		_settings.SettingChanged += change => {
			if (change.Key == "theme.active") {
				_bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", line => Console.WriteLine(line)));
			}
		};
		_themeOverrides.Changed += themeId => {
			if (themeId == (_settings.GetString("theme.active") ?? ThemeSettings.DefaultThemeId)) {
				_bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", line => Console.WriteLine(line)));
			}
		};

		// Layout: push the canonical document to the web when the store changes (a reconciled web edit or
		// a future MCP setLayout). Change events arrive off the main thread.
		_layout.Changed += _ => InvokeOnMainThread(PushLayoutToWeb);

		// Commands: wire the dispatcher to the web (Claude's runCommand of a web command posts run-command +
		// awaits its ack) and register the Core-side handlers (reopen terminal → restart the shell pane,
		// marshaled to the main thread).
		_commands.WebInvoker = InvokeWebCommandAsync;
		_commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			InvokeOnMainThread(() => _shell?.Restart());
			return Task.FromResult(CommandResult.Success("Reopened the terminal."));
		});
		// Toggle the window — the handler behind the global ctrl+` hotkey, and Claude's runCommand / the palette.
		_commands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			InvokeOnMainThread(ToggleWindow);
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});

		// Global hotkeys (e.g. ctrl+` → focus Weavie). The Carbon registrar registers them app-wide so they
		// fire even when Weavie is unfocused; the service reads the global bindings from _keybindings and
		// re-applies on edit. Disposed in WillTerminate (which unregisters the OS hotkeys).
		var hotkeyRegistrar = new MacGlobalHotkeys();
		hotkeyRegistrar.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_hotkeyService = new GlobalHotkeyService(_keybindings, _commands, hotkeyRegistrar);
		_hotkeyService.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Theme verb commands (install / install-from-file / select / undo / reset): Core handlers over the
		// app-global theme stores, with a native ".vsix" picker (NSOpenPanel) for install-from-file.
		ThemeCommands.RegisterHandlers(_commands, _settings, _themeOverrides, PickVsixFileAsync);

		// Keybindings (user-edited ~/.weavie/keybindings.json): re-push the catalog + resolved bindings so the
		// web rebuilds its resolver + palette live. PostToWeb marshals to the main thread itself.
		_keybindings.KeybindingsChanged += () => _bridge.PostToWeb(
			$"{{\"type\":\"commands\",\"commands\":{_keybindings.BuildCommandsJson()},"
			+ $"\"keybindings\":{_keybindings.BuildKeybindingsJson()}}}");

		_window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = "weavie",
			ContentView = _webView,
		};
		if (!usedSaved) {
			_window.Center();
		} else if (savedWindow is { Maximized: true }) {
			_window.Zoom(null);
		}

		// Persist geometry on resize-end and on close; SetWindow no-ops when nothing actually changed.
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidEndLiveResizeNotification, _ => SaveWindowState(), _window);
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => SaveWindowState(), _window);

		_window.MakeKeyAndOrderFront(null);

		nint screenHz = (_window.Screen ?? NSScreen.MainScreen)?.MaximumFramesPerSecond ?? 0;
		Console.WriteLine($"[weavie] NSScreen.maximumFramesPerSecond = {screenHz}");

		_webView.LoadRequest(new NSUrlRequest(new NSUrl("app://app/index.html")));

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot for the deliverable: fire from the native run loop (not a JS
		// timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
		// shipped app never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
			NSTimer.CreateScheduledTimer(delay, repeats: false, _ => CaptureSnapshot());
		}
	}

	/// <summary>Quits the app when its last (only) window is closed.</summary>
	public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

	/// <summary>Disposes both terminals and shuts down the IDE-MCP server on app exit.</summary>
	public override void WillTerminate(NSNotification notification) {
		SaveWindowState();
		// Fail any web command still awaiting an ack so a runCommand in flight at exit doesn't hang.
		foreach (var pending in _pendingWebCommands.Values) {
			pending.TrySetResult(CommandResult.Failure("The app is terminating before the command completed."));
		}

		_hotkeyService?.Dispose(); // unregisters the OS global hotkeys + removes the Carbon handler
		_claude?.Dispose();
		_shell?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_keybindings?.Dispose();
		_settings?.Dispose();
	}

	/// <summary>
	/// Toggles Weavie — the handler behind the global hotkey and <c>weavie.window.toggle</c>. When the app is
	/// active, hide it (focus returns to the previous app); otherwise activate + raise it. <c>Activate</c>
	/// cooperates with the window server (macOS 14+) and unhides a hidden app; <c>Hide</c> is the idiomatic
	/// macOS "send to background" (Cmd+H). Must run on the main thread.
	/// </summary>
	private void ToggleWindow() {
		var app = NSApplication.SharedApplication;
		if (app.Active) {
			app.Hide(this);
		} else {
			app.Activate();
			_window?.MakeKeyAndOrderFront(null);
		}
	}

	private void SaveWindowState() {
		if (_window is null || _layout is null || _window.IsMiniaturized) {
			return;
		}

		_layout.SetWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-zoomed restore bounds while zoomed.</summary>
	private LayoutGeometry CaptureWindowState() {
		if (_window!.IsZoomed && _layout!.Current.Window is { } prior) {
			return prior with { Maximized = true };
		}

		var frame = _window.Frame;
		return new LayoutGeometry {
			X = (int)frame.X,
			Y = (int)frame.Y,
			Width = (int)frame.Width,
			Height = (int)frame.Height,
			Maximized = _window.IsZoomed,
		};
	}

	private static bool IsOnScreen(CGRect frame) =>
		frame.Width > 0 && frame.Height > 0
		&& NSScreen.Screens.Any(screen => screen.VisibleFrame.IntersectsWith(frame));

	private void OnWebMessage(string json) {
		string type;
		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Console.WriteLine($"[weavie] (unparsed) {json}");
			return;
		}

		switch (type) {
			case "term-input":
				byte[] input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
				if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_INPUT"))) {
					string printable = string.Concat(input.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));
					Console.WriteLine($"[weavie] term-input <- xterm ({input.Length}B): {printable}");
					Console.Out.Flush();
				}
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
			case "open-editors-changed":
				_editor?.SetOpenEditors(OpenEditorTab.ParseList(root));
				break;
			case "new-scratch":
				// New File (Ctrl+N): create an untitled buffer + open it as a scratch tab.
				OpenNewScratch();
				break;
			case "save-scratch-as":
				// Ctrl+S on a scratch buffer: prompt for a real name (native Save panel), write it, drop the temp.
				SaveScratchAs(root);
				break;
			case "discard-scratch":
				// The user closed (and confirmed discarding) a scratch buffer: delete its temp file.
				_scratch?.Delete(root.TryGetProperty("path", out var dsEl) ? dsEl.GetString() ?? string.Empty : string.Empty);
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
				// The page's bridge listener is live; push the persisted layout so it restores on launch, and
				// the persisted editor session so the editor reopens its files.
				PushLayoutToWeb();
				PushEditorSessionToWeb();
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			case "editor-session-changed":
				HandleEditorSessionChanged(root);
				break;
			default:
				// log — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
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
			Console.WriteLine($"[weavie] layout-changed: bad document ({error})");
			return;
		}

		try {
			_layout.SetPanes(document.Root, document.Focused, LayoutSource.User);
		} catch (LayoutValidationException ex) {
			Console.WriteLine($"[weavie] layout-changed rejected: {ex.Message}");
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

	/// <summary>Applies an editor session the web sent (open files + view state) through the store, which persists it.</summary>
	private void HandleEditorSessionChanged(JsonElement root) {
		if (_editorSession is null || !root.TryGetProperty("session", out var sessionElement)) {
			return;
		}

		if (!EditorSessionSerialization.TryDeserialize(sessionElement.GetRawText(), out var session, out string? error)
			|| session is null) {
			Console.WriteLine($"[weavie] editor-session-changed: bad session ({error})");
			return;
		}

		_editorSession.Update(session);
	}

	/// <summary>Pushes the persisted editor session (with each open file's on-disk content) for launch restore.</summary>
	private void PushEditorSessionToWeb() {
		if (_editorSession is not null) {
			_bridge.PostToWeb(_editorSession.BuildRestoreJson());
		}
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>
	/// Pushes a live-refresh of one edited file. The editor's file models are VSCode working copies behind the
	/// host-backed <c>file://</c> provider, so the reload is driven by an <c>fs-change</c> push (the provider
	/// fires its change event → VSCode reloads the non-dirty model from disk). The legacy <c>refresh-file</c>
	/// message is no longer sent.
	/// </summary>
	private void PushRefreshToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(FileProviderProtocol.Changed(change.Path, "updated"));
		}
	}

	/// <summary>
	/// Pushes one file's per-turn diff so the page renders it inline in the live editor. Only in an auto-keep
	/// mode (acceptEdits/bypass), where the applied turn-markers are the review surface; in default mode openDiff
	/// is the per-edit review, so a second applied marker would just demand a redundant Accept — suppress it.
	/// </summary>
	private void PushTurnDiffToWeb(string path) {
		// _settings is set in DidFinishLaunching, before the change feed that drives this is ever wired, so it is
		// non-null by here — assert that (a violation throws loudly) rather than silently skipping the push.
		if (!PermissionModeDiffPresenter.AutoKeepsEdits(_settings!)) {
			return;
		}

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

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		// Built by hand (not JsonSerializer.Serialize, which is trim-unsafe — IL2026 — on the macOS target).
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
				Console.WriteLine($"[weavie] invoke-command {id} failed: {result.Error}");
			}
		} catch (Exception ex) when (ex is UnknownCommandException or InvalidOperationException) {
			Console.WriteLine($"[weavie] invoke-command {id} error: {ex.Message}");
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

	/// <summary>
	/// Native <c>.vsix</c> picker for the install-from-file theme command (NSOpenPanel on the main thread).
	/// Returns the chosen path, or null if the user cancelled. Called off the main thread by the command
	/// dispatcher, so the modal is marshaled onto it.
	/// </summary>
	private Task<string?> PickVsixFileAsync(CancellationToken ct) {
		var completion = new TaskCompletionSource<string?>();
		InvokeOnMainThread(() => {
			using var panel = new NSOpenPanel {
				Title = "Install Theme from .vsix",
				CanChooseFiles = true,
				CanChooseDirectories = false,
				AllowsMultipleSelection = false,
				AllowedFileTypes = ["vsix"],
			};
			completion.SetResult(panel.RunModal() == 1 && panel.Url is { Path: { } path } ? path : null);
		});
		return completion.Task;
	}

	/// <summary>Creates a new scratch (untitled) buffer and opens it as a scratch tab — the host side of New File (Ctrl+N).</summary>
	private void OpenNewScratch() {
		if (_scratch is null || _fileOpener is null) {
			return;
		}

		string path = _scratch.CreateNew();
		_fileOpener.Open(path, 1, preview: false, scratch: true);
	}

	/// <summary>
	/// Saves a scratch (untitled) buffer under a real name: a native <see cref="NSSavePanel"/> (defaulting to
	/// the workspace + the buffer's "Untitled-N" name), writes its content there, deletes the temp file, and
	/// replies <c>scratch-saved</c>. <c>reopen</c> is true only when the target is inside the workspace, so the
	/// editor reopens it as a normal working copy; saved elsewhere it's written + the user warned, but the editor
	/// can't edit out-of-workspace files, so the scratch tab just drops. The WKWebView raises script messages on
	/// the main thread, so the panel runs inline.
	/// </summary>
	private void SaveScratchAs(JsonElement root) {
		if (_scratch is null || _fileSystem is null || _workspace is null) {
			return;
		}

		string scratchPath = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? string.Empty : string.Empty;
		string content = root.TryGetProperty("content", out var cEl) ? cEl.GetString() ?? string.Empty : string.Empty;
		string suggested = root.TryGetProperty("suggestedName", out var nEl) ? nEl.GetString() ?? "Untitled" : "Untitled";

		string? target;
		using (var panel = new NSSavePanel {
			Title = "Save As",
			NameFieldStringValue = suggested,
			DirectoryUrl = NSUrl.FromFilename(_workspace),
		}) {
			target = panel.RunModal() == 1 && panel.Url is { Path: { } chosen } ? chosen : null;
		}

		if (string.IsNullOrEmpty(target)) {
			PostScratchSaved(scratchPath, string.Empty, reopen: false); // cancelled
			return;
		}

		try {
			_fileSystem.WriteAllText(target, content);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(target)}: {ex.Message}");
			PostScratchSaved(scratchPath, string.Empty, reopen: false);
			return;
		}

		_scratch.Delete(scratchPath);
		bool reopen = BufferStore.IsWithinWorkspace(_workspace, target);
		if (!reopen) {
			Notify("info", $"Saved {Path.GetFileName(target)} outside the workspace — it won't open in the editor.");
		}

		PostScratchSaved(scratchPath, target, reopen);
	}

	/// <summary>Replies to <c>save-scratch-as</c>: the saved path (empty when cancelled) + whether to reopen it.</summary>
	private void PostScratchSaved(string scratchPath, string savedPath, bool reopen) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "scratch-saved",
			scratchPath,
			savedPath,
			reopen,
		}));

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "notify", level, message }));

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

	/// <summary>Encodes a string as a JSON string literal (trim-safe; no reflection).</summary>
	private static string JsonString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	/// <summary>The correlation <c>id</c> of an fs-stat/read/write request.</summary>
	private static string FsId(JsonElement root) =>
		root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>The native <c>path</c> of an fs-stat/read/write request.</summary>
	private static string FsPath(JsonElement root) =>
		root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? string.Empty : string.Empty;

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
	}

	private void CaptureSnapshot() {
		string? dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		if (_webView is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		string? name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		string path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

		_webView.TakeSnapshot(new WKSnapshotConfiguration(), (image, error) => {
			if (image is null) {
				Console.Error.WriteLine($"[weavie] snapshot failed: {error?.LocalizedDescription}");
				return;
			}

			var tiff = image.AsTiff();
			if (tiff is null) {
				return;
			}

			using var rep = new NSBitmapImageRep(tiff);
			var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
			if (png is null) {
				Console.Error.WriteLine("[weavie] snapshot: PNG encoding failed");
				return;
			}

			if (png.Save(path, true, out var saveError)) {
				Console.WriteLine($"[weavie] snapshot saved: {path}");
			} else {
				Console.Error.WriteLine($"[weavie] snapshot save failed: {saveError?.LocalizedDescription}");
			}

			Console.Out.Flush();
		});
	}
}
