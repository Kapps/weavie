using System.Collections.Concurrent;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

/// <summary>
/// The shared, platform-agnostic host core every platform shell drives. Owns one workspace's Core graph and
/// session set, routes the page's web messages to the active session, and pushes state back over the bridge;
/// everything OS-specific is reached through an injected <see cref="IHostPlatform"/>. Split into three partials:
/// this file (lifecycle), <c>HostCore.WebBridge.cs</c> (message dispatch), and <c>HostCore.Sessions.cs</c>.
/// </summary>
public sealed partial class HostCore : IAsyncDisposable, ISessionHost {
	private readonly IHostPlatform _platform;
	private readonly IHostBridge _bridge;
	private readonly IUiDispatcher _ui;
	private readonly SettingsStore _settings;
	private readonly CommandRegistry _commandRegistry;
	private readonly KeybindingStore _keybindings;
	private readonly ThemeOverridesStore _themeOverrides;
	// App-global Claude-session-id map (keyed by cwd); each session resumes its own worktree's conversation.
	private readonly ClaudeSessionStore _claudeSessions;
	// App-global remote-agent registry; pushed to the page on `ready` and re-pushed on change (the web owns the
	// connections, this owns persistence — see remote-agents.ts).
	private readonly RemoteAgentStore _remoteAgents;
	// App-global session-rail UI state (last-used backend + promoted remote sessions); same push pattern.
	private readonly RailStateStore _railState;
	private readonly LayoutStore _layout;
	private readonly EditorSessionStore _editorSession;
	// In-flight web commands invoked by Claude (runCommand → run-command): token → completion, settled by the
	// web's command-ack (or a 5s timeout). Concurrent: acks arrive on the UI thread, the await is off it.
	private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pendingWebCommands = new();

	// Multi-session state: _session is the active backend, _primarySession the never-unloadable own checkout,
	// _sessions the rail's slots, _worktrees backs worktree-per-session, _pageOrigin pins later sessions' LSP origin.
	private HostSession? _session;
	private HostSession? _primarySession;
	private SessionManager? _sessions;
	private WorktreeManager? _worktrees;
	private ShellWorktreeProvisioner? _worktreeProvisioner;
	private string _pageOrigin = string.Empty;
	// The active session + first review file the page was told to auto-open this idle (see ShouldOpenReview),
	// so review opens once per idle yet re-arms on a new turn or a session switch. Null = nothing armed.
	private string? _armedReviewKey;

	// Drives the custom title bar (window-control / menu-action / file-index), present only when the platform
	// exposes an IShellWindow (a web-rendered title bar). Null on native-chrome hosts.
	private ShellController? _shell;
	// Global OS hotkeys, present only when the platform exposes a registrar. Disposed with the core.
	private GlobalHotkeyService? _hotkeys;
	// The app-global stores (settings / keybindings / theme overrides) may outlive a window (Windows), so the
	// reaction handlers are kept here and detached on dispose to avoid leaking this core into them.
	private Action? _onKeybindingsChanged;
	private Action<SettingChange>? _onSettingChanged;
	private Action<string>? _onThemeOverridesChanged;
	private Action? _onRemoteAgentsChanged;
	private Action? _onRailStateChanged;

	/// <summary>
	/// Builds only the cheap per-workspace stores (layout + editor session) so the shell can read the saved window
	/// geometry before creating its window; the heavy graph is built by <see cref="StartAsync"/>.
	/// </summary>
	public HostCore(IHostPlatform platform, HostServices services, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(platform);
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_platform = platform;
		_bridge = platform.Bridge;
		_ui = platform.Dispatcher;
		_settings = services.Settings;
		_commandRegistry = services.CommandRegistry;
		_keybindings = services.Keybindings;
		_themeOverrides = services.ThemeOverrides;
		_claudeSessions = services.ClaudeSessions;
		_remoteAgents = services.RemoteAgents;
		_railState = services.RailState;
		WorkspaceRoot = workspaceRoot;
		Id = WorkspaceId.ForPath(workspaceRoot);

		// Per-workspace layout + editor session, keyed by the folder's path id so each folder restores its own state.
		_layout = LayoutPanes.CreateStore(WeaviePaths.WorkspaceLayoutFile(Id));
		_editorSession = new EditorSessionStore(new LocalFileSystem(), WeaviePaths.WorkspaceEditorSessionFile(Id));
		_editorSession.Log += Log;
	}

	/// <summary>
	/// Raised when the page sends <c>ready</c>, after the core pushed its restore state. A shell with a
	/// web-rendered title bar subscribes to push the initial native window state (which only it knows). UI thread.
	/// </summary>
	public event Action? Ready;

	/// <summary>This workspace's stable identity (path-derived).</summary>
	public WorkspaceId Id { get; }

	/// <summary>The absolute workspace root this core serves.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The saved window geometry for this workspace, or <c>null</c> when there's none (the shell centers a default).</summary>
	public WindowState? SavedWindow => _layout.Current.Window;

	/// <summary>Persists the window geometry the shell captured (size / position / maximized).</summary>
	public void SaveWindow(WindowState state) {
		ArgumentNullException.ThrowIfNull(state);
		_layout.SetWindow(state);
	}

	/// <summary>The folder's leaf name for the window title / shell label (e.g. <c>weavie</c> for <c>/src/weavie</c>).</summary>
	public string WorkspaceLabel => WorkspaceNaming.Label(WorkspaceRoot);

	/// <summary>
	/// Builds the workspace's live backend: the primary session, the session set (pre-existing worktrees
	/// reconciled into dormant chips), the title-bar controller + global hotkeys where supported, and the store
	/// reactions. Call once, after the bridge is attached and <paramref name="pageOrigin"/> is resolved.
	/// </summary>
	public async Task StartAsync(string pageOrigin) {
		ArgumentException.ThrowIfNullOrEmpty(pageOrigin);
		_pageOrigin = pageOrigin;

		// A Finder-launched .app inherits launchd's minimal PATH; import the login-shell PATH before anything
		// spawns so directly-launched children (LSP servers, git) resolve as they would from a terminal. No-op
		// except on the bundled Mac app.
		await LoginShellPath.ImportOnceAsync(line => Log($"[path] {line}")).ConfigureAwait(false);

		_bridge.MessageReceived += OnWebMessage;

		// The primary session: the workspace's own checkout. Built once pageOrigin is known so its LSP
		// WebSocket origin is pinned correctly. CreateSession wires its handlers + gated push subscriptions.
		_primarySession = CreateSession(WorkspaceRoot);
		_session = _primarySession;
		// The active session drives the page's single editor: unmute its editor output (sessions start muted).
		_primarySession.SetEditorOutputActive(true);
		// Seed the primary session's in-memory editor state from its persisted store, so switching away and
		// back restores the same tabs (secondary worktree sessions start empty and live only for the window).
		_primarySession.EditorSession = _editorSession.Current;
		// Garbage-collect scratch (untitled) temp files orphaned by a crash — keep only those still referenced
		// by the restored editor session (they reopen as their "Untitled-N" tabs).
		_primarySession.Scratch.GarbageCollect(
			_editorSession.Current.Open.Where(entry => entry.Scratch).Select(entry => entry.Path));

		string primaryLabel = await ResolvePrimaryLabelAsync().ConfigureAwait(false);

		// Title bar: route the web title-bar messages to the shared controller, but only when the platform
		// renders one (web custom chrome). Native-chrome hosts leave _shell null and those messages no-op.
		if (_platform.Window is { } window) {
			_shell = new ShellController(window, _primarySession.FileIndex, _bridge.PostToWeb);
		}

		// Sessions: the worktree manager + slot set, the primary (always-loaded) slot, then reconcile
		// pre-existing worktrees into dormant slots so none leak. The rail's session list is pushed on the
		// page's `ready` message (PostToWeb before navigation no-ops).
		_worktrees = await BuildWorktreeManagerAsync().ConfigureAwait(false);
		_sessions = new SessionManager(_worktrees);
		AddPrimarySlot(primaryLabel);
		await ReconcileWorktreesOnOpenAsync().ConfigureAwait(false);

		// Global hotkeys (e.g. ctrl+` → toggle the window): the service reads the global bindings and dispatches
		// to the primary session's command dispatcher (the always-loaded one), where the handlers are wired.
		if (_platform.HotkeyRegistrar is { } registrar) {
			_hotkeys = new GlobalHotkeyService(_keybindings, _primarySession.Commands, registrar);
			_hotkeys.Log += Log;
		}

		WireReactions();
	}

	/// <summary>
	/// The page-bootstrap script the shell injects at document-start: resolved fonts, editor options, theme, LSP
	/// discovery, command catalog + keybindings, and shell config. Identical on every host (only the injection
	/// differs); the headless shell prepends its own <c>__WEAVIE_BRIDGE_WS__</c>. Call after <see cref="StartAsync"/>.
	/// </summary>
	public string BuildBootstrap() {
		string lsp = _primarySession?.LspConfigJson ?? "null";
		return
			$"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings, messageType: null)};"
			+ $"window.__WEAVIE_EDITOR_OPTIONS__ = {EditorSettings.BuildJson(_settings, messageType: null)};"
			+ $"window.__WEAVIE_THEME__ = {ThemeJson.Build(_settings, _themeOverrides, messageType: null, log: Log)};"
			+ $"window.__WEAVIE_LSP__ = {lsp};"
			+ $"window.__WEAVIE_COMMANDS__ = {_keybindings.BuildCommandsJson()};"
			+ $"window.__WEAVIE_KEYBINDINGS__ = {_keybindings.BuildKeybindingsJson()};"
			+ ShellProtocol.BuildConfigScript(_platform.ChromePlatform, _platform.TitleBar, WorkspaceLabel, _platform.Recents);
	}

	/// <summary>Pushes the title bar's current window state (maximize glyph + blur dim); no-op on native-chrome hosts.</summary>
	public void PushWindowState(bool maximized, bool focused) => _shell?.PushWindowState(maximized, focused);

	/// <summary>
	/// Wires the live reactions to store changes: a changed shell reopens the terminal; font/editor/theme/
	/// keybinding/layout edits re-push their resolved values.
	/// </summary>
	private void WireReactions() {
		// A changed shell (ApplyMode.ReopensTerminal) reopens the active session's shell pane live.
		_settings.Subscribe("terminal.shell", _ => _ui.Post(() => _session?.Shell.Restart()));

		// Fonts / editor options / theme (ApplyMode.Live): re-push the resolved values so the web applies them in
		// place. PostToWeb marshals to the UI thread and the stores are thread-safe, so call it directly.
		_onSettingChanged = change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}

			if (EditorSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(EditorSettings.BuildJson(_settings, "editorOptions"));
			}

			if (ThemeSettings.Keys.Contains(change.Key)) {
				PushThemeToWeb();
			}
		};
		_settings.SettingChanged += _onSettingChanged;
		_onThemeOverridesChanged = themeId => {
			if (ThemeSettings.IsSelectedThemeId(_settings, themeId)) {
				PushThemeToWeb();
			}
		};
		_themeOverrides.Changed += _onThemeOverridesChanged;

		// Keybindings (user-edited ~/.weavie/keybindings.json): re-push the catalog + resolved bindings so the
		// web rebuilds its resolver + palette live. Detached on dispose (the store may outlive this core).
		_onKeybindingsChanged = () => _bridge.PostToWeb(
			$"{{\"type\":\"commands\",\"commands\":{_keybindings.BuildCommandsJson()},"
			+ $"\"keybindings\":{_keybindings.BuildKeybindingsJson()}}}");
		_keybindings.KeybindingsChanged += _onKeybindingsChanged;

		// Remote agents: a connect/disconnect (in this window or another sharing the app-global store) re-pushes
		// the registry so every page's rail + New Session location list stays in sync. PostToWeb marshals itself.
		_onRemoteAgentsChanged = PushRemoteAgentsToWeb;
		_remoteAgents.Changed += _onRemoteAgentsChanged;

		// Session rail UI state (last-used backend + promoted remotes): same re-push-on-change as remote agents.
		_onRailStateChanged = PushRailStateToWeb;
		_railState.Changed += _onRailStateChanged;

		// Layout: when the store changes (a reconciled web edit, or an MCP setLayout), push the canonical
		// document back so the web re-renders. Change events arrive off the UI thread.
		_layout.Changed += _ => _ui.Post(PushLayoutToWeb);
	}

	// Re-pushes the resolved theme (settings + overrides) so the web applies it live.
	private void PushThemeToWeb() => _bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", Log));

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		if (_onKeybindingsChanged is not null) {
			_keybindings.KeybindingsChanged -= _onKeybindingsChanged;
			_onKeybindingsChanged = null;
		}

		if (_onSettingChanged is not null) {
			_settings.SettingChanged -= _onSettingChanged;
			_onSettingChanged = null;
		}

		if (_onThemeOverridesChanged is not null) {
			_themeOverrides.Changed -= _onThemeOverridesChanged;
			_onThemeOverridesChanged = null;
		}

		if (_onRemoteAgentsChanged is not null) {
			_remoteAgents.Changed -= _onRemoteAgentsChanged;
			_onRemoteAgentsChanged = null;
		}

		if (_onRailStateChanged is not null) {
			_railState.Changed -= _onRailStateChanged;
			_onRailStateChanged = null;
		}

		_hotkeys?.Dispose(); // unregisters the OS global hotkeys

		// Fail any web command still awaiting an ack so a runCommand in flight at close doesn't hang.
		foreach (var pending in _pendingWebCommands.Values) {
			pending.TrySetResult(CommandResult.Failure("The window closed before the command completed."));
		}

		_pendingWebCommands.Clear();
		if (_sessions is not null) {
			await _sessions.DisposeAsync().ConfigureAwait(false);
		} else if (_primarySession is not null) {
			await _primarySession.DisposeAsync().ConfigureAwait(false);
		}
	}
}
