using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CoreGraphics;
using Foundation;
using UniformTypeIdentifiers;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
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
public sealed partial class AppDelegate : NSApplicationDelegate {
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
	// Contextual file browser + flat file index (the omnibar "Go to File" source), both rooted at the
	// workspace; the macOS counterparts of the Windows HostSession's Browser/FileIndex.
	private WorkspaceBrowser? _browser;
	private WorkspaceFileIndex? _fileIndex;
	// Recent workspaces (~/.weavie/recents.json) for File ▸ Open Recent and the omnibar shell config.
	private RecentWorkspaces? _recents;
	// Loopback WS↔stdio language-server proxy; its config (port/token/workspace/catalog) is injected as
	// window.__WEAVIE_LSP__ so the page starts a monaco-languageclient per language and shows the file tree.
	private LspBridgeServer? _lsp;
	private string? _workspace;
	private NSWindow? _window;
	private WKWebView? _webView;
#if DEBUG
	// Debug-only Vite dev server for hot reload; null in Release (the type is compiled out there).
	private WebDevServer? _webDev;
#endif
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
		// Render at the display's full refresh (120Hz) instead of WKWebView's default 60fps pacing.
		WebKitFeatureFlags.DisablePrefer60Fps(config.Preferences);

		// Restore the saved window geometry (size / position / zoomed) when present and on-screen, else
		// fall back to a centered 1280x840 default.
		_layout = LayoutPanes.CreateStore(filePath: null);
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
		_settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		_settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Commands + keybindings: the app's command catalog (CoreCommands) and the user keybindings resolved
		// from ~/.weavie/keybindings.json over the defaults. The dispatcher routes runCommand (MCP) +
		// invoke-command (web) to Core/web handlers; the WebInvoker + Core handlers are wired below.
		_commandRegistry = CoreCommands.CreateRegistry();
		_keybindings = new KeybindingStore(_commandRegistry, filePath: null, enableWatcher: true);
		_keybindings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_commands = new CommandDispatcher(_commandRegistry);

		// Per-theme color overrides (~/.weavie/theme-overrides.json); the active theme itself is a setting.
		_themeOverrides = new ThemeOverridesStore(new LocalFileSystem(), path: null);
		_themeOverrides.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Typography: inject the resolved editor + terminal fonts at document start so both surfaces
		// mount at the user's font with no default-font flash; live changes are pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings, messageType: null)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		// Editor behavior: inject the resolved editor.* options at document start so the editor mounts with
		// the user's options (inlay hints, word wrap, hover delay, …); live changes are pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_EDITOR_OPTIONS__ = {EditorSettings.BuildJson(_settings, messageType: null)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		// Theme: inject the active theme (id + override ops, plus the converted JSON for installed themes)
		// at document start so the editor / terminal / chrome mount themed with no flash; live changes pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_THEME__ = {ThemeJson.Build(_settings, _themeOverrides, messageType: null, log: line => Console.WriteLine(line))};"),
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
		// File browser (contextual tree) + flat file index (omnibar quick-open), both rooted at the workspace.
		_browser = new WorkspaceBrowser(fileSystem, workspace);
		_fileIndex = new WorkspaceFileIndex(fileSystem, workspace);
		_fileIndex.Log += line => {
			Console.WriteLine($"[index] {line}");
			Console.Out.Flush();
		};
		// Recents: record this workspace and surface the list in File ▸ Open Recent + the omnibar shell config.
		_recents = new RecentWorkspaces(fileSystem, path: null);
		_recents.Add(workspace);
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
		// Built before the IDE-MCP server so its EditLocationFor can back the hook bridge's edit jump-links
		// (the bridge decision runs after the tracker has folded in the PostToolUse content).
		_changes = new SessionChangeTracker(fileSystem);
		_ide = new IdeIntegration(
			new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout, _editor,
			commands: _commands, keybindings: _keybindings, themeOverrides: _themeOverrides,
			editLocator: _changes.EditLocationFor);
		_ide.Server.Log += line => {
			Console.WriteLine($"[mcp] {line}");
			Console.Out.Flush();
		};
		_ide.RegistryServer?.Log += line => {
			Console.WriteLine($"[registry] {line}");
			Console.Out.Flush();
		};

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
		// list; FileChanged pushes a targeted live-refresh of the one edited file. These events are raised
		// synchronously on the hook bridge's accept-loop thread (Observe → Changed/FileChanged/TurnBegan), so
		// marshal onto the main thread ASYNC (BeginInvokeOnMainThread), never the synchronous InvokeOnMainThread:
		// HookBridgeServer.DisposeAsync awaits that accept loop, and WillTerminate disposes it on a parked main
		// thread — a synchronous hop here would block the accept-loop thread on the main thread while the main
		// thread blocks on the accept loop, a hard deadlock (the app freezing on Cmd+Q). PostToWeb marshals on
		// its own anyway, so the hop only needs to be non-blocking.
		_ide.HookBridge.Observed += _changes.Observe;
		_changes.Changed += () => BeginInvokeOnMainThread(PushChangesToWeb);
		_changes.FileChanged += path => BeginInvokeOnMainThread(() => PushRefreshToWeb(path));
		// Inline diff: per-turn diff per edited file + clear-all on a turn boundary (implicit accept).
		_changes.FileChanged += path => BeginInvokeOnMainThread(() => PushTurnDiffToWeb(path));
		_changes.TurnBegan += () => BeginInvokeOnMainThread(PushTurnReset);
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the main thread (an MCP setSetting runs on the server's background
		// thread), so marshal onto it before touching the controller — but ASYNC (BeginInvokeOnMainThread),
		// never the synchronous InvokeOnMainThread. Restart() tears the PTY down (kill + close) and the read
		// thread then blocks in waitpid; a *synchronous* hop would park the background thread on the main
		// thread across that teardown — the same hard deadlock HostBridge.PostToWeb documents and avoids.
		_settings.Subscribe("terminal.shell", _ => BeginInvokeOnMainThread(() => _shell?.Restart()));

		// Fonts (ApplyMode.Live): any global or per-surface font change re-pushes the resolved editor +
		// terminal fonts to the web app, which applies them in place. PostToWeb marshals to the main
		// thread itself and the store is thread-safe, so the off-thread change event can call it directly.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}
		};

		// Editor options (ApplyMode.Live): any editor.* change re-pushes the resolved options so the web
		// applies them via updateOptions in place.
		_settings.SettingChanged += change => {
			if (EditorSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(EditorSettings.BuildJson(_settings, "editorOptions"));
			}
		};

		// Theme (ApplyMode.Live): a mode/theme switch (theme.mode|theme.light|theme.dark) or an override edit on
		// either selected theme re-pushes the resolved theme pair so the web re-themes the editor, terminal, and
		// chrome in place. PostToWeb marshals to the main thread and the stores are thread-safe, so the
		// off-thread events can call it directly.
		_settings.SettingChanged += change => {
			if (ThemeSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", line => Console.WriteLine(line)));
			}
		};
		_themeOverrides.Changed += themeId => {
			if (ThemeSettings.IsSelectedThemeId(_settings, themeId)) {
				_bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", line => Console.WriteLine(line)));
			}
		};

		// Layout: push the canonical document to the web when the store changes (a reconciled web edit or
		// a future MCP setLayout). Change events arrive off the main thread; marshal async (never the
		// synchronous InvokeOnMainThread — see the change-feed note above for the Cmd+Q deadlock it avoids).
		_layout.Changed += _ => BeginInvokeOnMainThread(PushLayoutToWeb);

		// Commands: wire the dispatcher to the web (Claude's runCommand of a web command posts run-command +
		// awaits its ack) and register the Core-side handlers (reopen terminal → restart the shell pane,
		// marshaled to the main thread).
		_commands.WebInvoker = InvokeWebCommandAsync;
		_commands.RegisterHandler(CoreCommands.ReopenTerminal, (_, _) => {
			// Async hop (see the terminal.shell reaction above): a command can be dispatched from the MCP
			// thread, and a synchronous hop across the PTY teardown deadlocks.
			BeginInvokeOnMainThread(() => _shell?.Restart());
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

		// Shell config: tell the web to render the macOS title-bar mode (the omnibar quick-open strip; the
		// native chrome already provides the window controls + menu bar). Injected before navigation so the
		// omnibar mounts with no flash. The workspace label + recents feed the omnibar and File ▸ Open Recent.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_SHELL__ = {BuildShellConfigJson(workspace)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		// Native menu bar: the macOS counterpart of the Windows web title bar's File/View menus, plus the
		// standard App/Edit/Window menus. File/View items dispatch the same Weavie command ids the keyboard
		// and omnibar use; their shortcuts are read from the keybinding store so a rebind keeps them in sync.
		NSApplication.SharedApplication.MainMenu = MacAppMenu.Build(
			runCommand: id => InvokeCommandFromWeb(id, argsJson: null),
			resolveChord: ResolveChord,
			openFolder: OpenFolderInteractive,
			openRecent: SwitchWorkspace,
			recents: _recents.Items);

		_window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = $"{WorkspaceLabel(workspace)} — weavie",
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

		// Resolve the page origin (Debug: the Vite dev server; Release: the bundled app:// wwwroot), start the
		// LSP bridge pinned to that origin, inject window.__WEAVIE_LSP__, and navigate. Fire-and-forget so the
		// dev-server readiness poll (Debug) doesn't block launch; the load is marshaled back to the main thread.
		_ = LoadWebAppAsync();

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot for the deliverable: fire from the native run loop (not a JS
		// timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
		// shipped app never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
			NSTimer.CreateScheduledTimer(delay, repeats: false, _ => CaptureSnapshot());
		}
	}

	/// <summary>
	/// Resolves the page origin, starts the LSP bridge pinned to it, injects <c>window.__WEAVIE_LSP__</c>,
	/// and navigates. In Debug the origin is the Vite dev server (hot-module reload) when reachable, else the
	/// bundled <c>app://</c> wwwroot; Release always serves the bundle. Fire-and-forget from launch: the
	/// dev-server readiness poll runs off the main thread, and the script injection + navigation are marshaled
	/// back onto it. The LSP origin must equal the page origin so the WS upgrade's <c>Origin</c> check passes.
	/// </summary>
	private async Task LoadWebAppAsync() {
		string pageOrigin = "app://app";
#if DEBUG
		_webDev = new WebDevServer(line => {
			Console.WriteLine($"[vite] {line}");
			Console.Out.Flush();
		});
		string? devOrigin = await _webDev.StartAsync().ConfigureAwait(false);
		if (devOrigin is not null) {
			pageOrigin = devOrigin;
			Console.WriteLine($"[weavie] hot reload: serving web from {devOrigin} (Vite dev server)");
		} else {
			Console.WriteLine("[weavie] dev server unavailable; loading bundled wwwroot at app://app");
		}

		Console.Out.Flush();
#endif

		// LSP bridge: a loopback WS↔stdio proxy that spawns language servers (bring-your-own, resolved on
		// PATH) for monaco-languageclient in the page. Bind 127.0.0.1, require the per-session token on the
		// upgrade, and pin the allowed origin to the page so only our page can connect. The workspace root in
		// the config also drives the page's WORKSPACE_ROOT (so the file tree appears). Off the main thread.
		string lspConfigJson = "null";
		if (_workspace is not null) {
			string lspToken = IdeLockFile.NewAuthToken();
			_lsp = new LspBridgeServer(lspToken, _workspace, allowedOrigin: pageOrigin, resolveDescriptor: null);
			_lsp.Log += line => {
				Console.WriteLine($"[lsp] {line}");
				Console.Out.Flush();
			};
			// Forward the workspace watcher's on-disk change batches to the page's file:// provider so the
			// editor reloads working copies touched outside Claude (another editor, a git checkout).
			_lsp.FileChanges += PushWatcherChangesToWeb;
			int lspPort = _lsp.Start();
			lspConfigJson = BuildLspConfigJson(lspPort, lspToken, _workspace);
			Console.WriteLine($"[weavie] LSP bridge on 127.0.0.1:{lspPort}; workspace {_workspace}");
			Console.Out.Flush();
		}

		InvokeOnMainThread(() => {
			if (_webView is null) {
				return;
			}

			// Inject the LSP discovery config before navigation so the page can lazily start a client per
			// language on the first matching document (and show the file tree rooted at the workspace).
			_webView.Configuration.UserContentController.AddUserScript(new WKUserScript(
				new NSString($"window.__WEAVIE_LSP__ = {lspConfigJson};"),
				WKUserScriptInjectionTime.AtDocumentStart,
				isForMainFrameOnly: true));
			_webView.LoadRequest(new NSUrlRequest(new NSUrl($"{pageOrigin}/index.html")));
		});
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
#if DEBUG
		_webDev?.Dispose(); // kills the Vite dev server this run spawned (a reused one is left alone)
#endif
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_lsp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
