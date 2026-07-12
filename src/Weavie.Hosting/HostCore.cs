using System.Collections.Concurrent;
using System.Reflection;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Diagnostics;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Suggestions;
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
	private readonly HostRuntimeInfo _runtime;
	private readonly IHostBridge _bridge;
	private readonly IUiDispatcher _ui;
	private readonly SettingsStore _settings;
	private readonly CommandRegistry _commandRegistry;
	private readonly SuggestionRegistry _suggestionRegistry;
	private readonly KeybindingStore _keybindings;
	private readonly ThemeOverridesStore _themeOverrides;
	// App-global Claude-session-id map (keyed by cwd); each session resumes its own worktree's conversation.
	private readonly AgentProviderRegistry _agentProviders;
	// App-global remote-agent registry; pushed to the page on `ready` and re-pushed on change (the web owns the
	// connections, this owns persistence — see remote-agents.ts).
	private readonly RemoteAgentStore _remoteAgents;
	// App-global session-rail UI state (last-used backend + promoted remote sessions); same push pattern.
	private readonly RailStateStore _railState;
	// App-global captured console output (stdout/stderr teed into a bounded ring), served by the in-app log viewer.
	private readonly LogBuffer _logBuffer;
	// Lists open PRs for the Open-PR flow (GitHub by default; a static stub under the headless harness).
	private readonly Weavie.Core.Review.IPullRequestProvider _pullRequests;
	// Loads/posts a PR's review comments (same GitHub client, or the harness stub).
	private readonly Weavie.Core.Review.IReviewCommentStore _reviewComments;
	// The source system (Notion personal-access-token validate + fetch); see HostCore.Sources.cs.
	private readonly Weavie.Core.Sources.ISourceConnector _sources;
	private readonly LayoutStore _layout;
	private readonly EditorSessionStore _editorSession;
	// Per-workspace loaded/active overlay (which worktree sessions were loaded, which was active), so a reopen —
	// including a worker auto-update restart — comes back as the user left it. See HostCore.SessionState.cs.
	private readonly SessionStore _sessionStore;
	private readonly RecentFilesStore _recentFiles;
	private readonly CorrectionCorpus _corrections;
	// In-flight web commands invoked by Claude (runCommand → run-command): token → completion, settled by the
	// web's command-ack (or a 5s timeout). Concurrent: acks arrive on the UI thread, the await is off it.
	private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pendingWebCommands = new();

	// Multi-session state: _session is the active backend, _primarySession the never-unloadable own checkout,
	// _sessions the rail's slots, _worktrees backs worktree-per-session.
	private HostSession? _session;
	private HostSession? _primarySession;
	private SessionManager? _sessions;
	private WorktreeManager? _worktrees;
	private ShellWorktreeProvisioner? _worktreeProvisioner;
	// StartAsync is idempotent: the Windows shell kicks it off early to overlap the slow WebView2 environment
	// creation, and the web launcher awaits it again — both join this one run.
	private readonly object _startGate = new();
	private Task? _startTask;

	// Drives the custom title bar (window-control / menu-action / file-index), present only when the platform
	// exposes an IShellWindow (a web-rendered title bar). Null on native-chrome hosts.
	private ShellController? _shell;
	// Global OS hotkeys, present only when the platform exposes a registrar. Disposed with the core.
	private GlobalHotkeyService? _hotkeys;
	// The app-global stores (settings / keybindings / theme overrides) may outlive a window (Windows), so the
	// reaction handlers are kept here and detached on dispose to avoid leaking this core into them.
	private Action? _onKeybindingsChanged;
	private Action<IReadOnlyList<string>>? _onUnknownKeybindingCommands;
	private Action<bool>? _onKeybindingsMalformedChanged;
	private Action<SettingChange>? _onSettingChanged;
	private Action<bool>? _onSettingsMalformedChanged;
	private Action<string>? _onThemeOverridesChanged;
	private Action? _onRemoteAgentsChanged;
	private Action? _onRailStateChanged;
	private IDisposable? _shellSettingSubscription;

	/// <summary>
	/// Builds only the cheap per-workspace stores (layout + editor session) so the shell can read the saved window
	/// geometry before creating its window; the heavy graph is built by <see cref="StartAsync"/>.
	/// </summary>
	public HostCore(IHostPlatform platform, HostServices services, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(platform);
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_platform = platform;
		// The build a managed worker actually loaded (its own versions/<build>/ path), or the dev version — surfaced
		// to the embedded claude so it knows whether it's a remote worker and on which build. See HostRuntimeInfo.
		_runtime = HostRuntimeInfo.Resolve(platform.Transport, AppContext.BaseDirectory, BuildNumber);
		_bridge = platform.Bridge;
		_ui = platform.Dispatcher;
		_settings = services.Settings;
		_commandRegistry = services.CommandRegistry;
		_suggestionRegistry = services.SuggestionRegistry;
		_keybindings = services.Keybindings;
		_themeOverrides = services.ThemeOverrides;
		_agentProviders = services.AgentProviders;
		_remoteAgents = services.RemoteAgents;
		_railState = services.RailState;
		_logBuffer = services.LogBuffer;
		_pullRequests = services.PullRequests;
		_reviewComments = services.ReviewComments;
		_sources = services.Sources;
		WorkspaceRoot = workspaceRoot;
		Id = WorkspaceId.ForPath(workspaceRoot);

		// Back per-workspace settings (worktree.setupCommand, test.profile) from the workspace's out-of-repo overlay.
		// On single-workspace hosts the store gets one workspace; on Windows the shared store gets one per window.
		_settings.RegisterWorkspace(workspaceRoot);

		// Per-workspace layout + editor session, keyed by the folder's path id so each folder restores its own state.
		_layout = LayoutPanes.CreateStore(WeaviePaths.WorkspaceLayoutFile(Id));
		_editorSession = new EditorSessionStore(new LocalFileSystem(), WeaviePaths.WorkspaceEditorSessionFile(Id));
		_editorSession.Log += Log;
		_sessionStore = new SessionStore(new LocalFileSystem(), WeaviePaths.WorkspaceSessionsFile(Id));
		_sessionStore.Log += Log;
		_recentFiles = new RecentFilesStore(new LocalFileSystem(), WeaviePaths.WorkspaceRecentFilesFile(Id));
		_recentFiles.Log += Log;
		// One correction ring per workspace, shared by every session/worktree: rules about how the agent codes
		// in this repo are repo-level. Its count gates the corrections.learn card, so changes re-evaluate.
		_corrections = new CorrectionCorpus(new LocalFileSystem(), WeaviePaths.WorkspaceCorrectionsFile(Id));
		_corrections.Log += Log;
		_corrections.Changed += () => _suggestions?.Evaluate();
	}

	// The last file recorded as recent, so the active-editor stream (which re-fires on every cursor move within a
	// file) bumps frecency once per distinct file visit, not per move.
	private string? _lastRecentPath;

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
	/// reactions. Idempotent — the shell may kick it off early (to overlap WebView2 bring-up) and the web launcher
	/// awaits it again; both join one run. Call after the bridge is attached.
	/// </summary>
	public Task StartAsync() {
		lock (_startGate) {
			return _startTask ??= StartCoreAsync();
		}
	}

	private async Task StartCoreAsync() {
		// Record any unhandled background-thread exception to a crash log (and stderr) before the runtime tears
		// down, so a hard exit leaves a trace instead of vanishing; surfaced as a toast on the next launch.
		CrashReporter.Install(line => Log($"[crash] {line}"));

		// Any launch context can carry a truncated environment and a stingy open-file limit — a Finder .app or
		// desktop entry via launchd, a headless host under a supervisor. Raise the descriptor limit so a second
		// session can't exhaust it mid-switch, and import the login-shell environment so spawned children (LSP
		// servers, git) resolve as from a terminal. Both no-op on Windows and when nothing needs raising.
		PosixFileLimit.RaiseToHardLimit(line => Log($"[fd] {line}"));
		await LoginShellEnvironment.ImportOnceAsync(line => Log($"[env] {line}")).ConfigureAwait(false);

		_bridge.MessageReceived += OnWebMessage;

		// The primary session: the workspace's own checkout. Built after the login-shell env import so its language
		// servers + git resolve from PATH. CreateSession wires its handlers + gated push subscriptions.
		_primarySession = CreateSession(WorkspaceRoot, "claude");
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

		// One git probe shared by the rail label and the worktree manager (was two redundant is-repo calls).
		var (git, isRepo) = await ProbeGitAsync().ConfigureAwait(false);
		string primaryLabel = await ResolvePrimaryLabelAsync(git, isRepo).ConfigureAwait(false);

		// Title bar: route the web title-bar messages to the shared controller, but only when the platform
		// renders one (web custom chrome). Native-chrome hosts leave _shell null and those messages no-op.
		if (_platform.Window is { } window) {
			_shell = new ShellController(window, _primarySession.FileIndex, _bridge.PostToWeb);
		}

		// Sessions: the worktree manager + slot set, the primary (always-loaded) slot, then reconcile
		// pre-existing worktrees into dormant slots so none leak. The rail's session list is pushed on the
		// page's `ready` message (PostToWeb before navigation no-ops).
		_worktrees = isRepo ? BuildWorktreeManager(git) : null;
		_sessions = new SessionManager(_worktrees);
		AddPrimarySlot(primaryLabel);
		await ReconcileWorktreesOnOpenAsync().ConfigureAwait(false);
		// Overlay the persisted loaded/active state onto the reconciled chips: reload the sessions that were live
		// at last close (each --resumes) and re-activate the last-active one, so a reopen/update-restart is seamless.
		RestoreSessionState();

		// Contextual suggestions: the manifest probe runs off the hot path; the active set is pushed on `ready`.
		InitSuggestions();

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
			string.Concat(LiveSettingGroups.Select(g => $"window.{g.Global} = {g.Build(_settings, null)};"))
			+ $"window.__WEAVIE_THEME__ = {ThemeJson.Build(_settings, _themeOverrides, messageType: null, log: Log)};"
			+ $"window.__WEAVIE_LSP__ = {lsp};"
			+ BuildTestProfileScript()
			+ $"window.__WEAVIE_COMMANDS__ = {_keybindings.BuildCommandsJson()};"
			+ $"window.__WEAVIE_KEYBINDINGS__ = {_keybindings.BuildKeybindingsJson()};"
			+ ShellProtocol.BuildConfigScript(_platform.ChromePlatform, _platform.TitleBar, WorkspaceLabel, _platform.Recents, BuildNumber);
	}

	// Live settings groups: each is injected pre-navigation as window.{Global} and re-pushed as its
	// MessageType when any of its Keys changes. One row per group — the bootstrap and the change handler
	// both iterate this table.
	private static readonly (IReadOnlyList<string> Keys, string MessageType, string Global,
		Func<SettingsStore, string?, string> Build)[] LiveSettingGroups = [
		(FontSettings.Keys, "fonts", "__WEAVIE_FONTS__", FontSettings.BuildJson),
		(NotificationSettings.Keys, "notification-prefs", "__WEAVIE_NOTIFICATIONS__", NotificationSettings.BuildJson),
		(EditorSettings.Keys, "editorOptions", "__WEAVIE_EDITOR_OPTIONS__", EditorSettings.BuildJson),
		(AgentSettings.Keys, "agent-defaults", "__WEAVIE_AGENT__", AgentSettings.BuildJson),
	];

	/// <summary>The app's build identity (SemVer with the build number as patch, e.g. <c>0.1.247</c>), stamped at build time.</summary>
	public static string BuildNumber =>
		typeof(HostCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? throw new InvalidOperationException("Weavie.Hosting has no AssemblyInformationalVersion — the build-stamp target did not run.");

	/// <summary>Pushes the title bar's current window state (maximize glyph + blur dim); no-op on native-chrome hosts.</summary>
	public void PushWindowState(bool maximized, bool focused) => _shell?.PushWindowState(maximized, focused);

	/// <summary>
	/// Wires the live reactions to store changes: a changed shell reopens the terminal; font/editor/theme/
	/// keybinding/layout edits re-push their resolved values.
	/// </summary>
	private void WireReactions() {
		// A changed shell (ApplyMode.ReopensTerminal) reopens the active session's shell pane live.
		_shellSettingSubscription = _settings.Subscribe("terminal.shell", _ => _ui.Post(() => _session?.Shell.Restart()));

		// Live settings groups + theme: re-push the resolved values so the web applies them in place.
		// PostToWeb marshals to the UI thread and the stores are thread-safe, so call it directly.
		_onSettingChanged = change => {
			foreach (var (keys, messageType, _, build) in LiveSettingGroups) {
				if (keys.Contains(change.Key)) {
					_bridge.PostToWeb(build(_settings, messageType));
				}
			}

			if (ThemeSettings.Keys.Contains(change.Key)) {
				PushThemeToWeb();
			}

			// Configuring the worktree setup command or the test profile can make the workspace-setup card vanish;
			// re-evaluate the suggestions. A changed test profile also re-pushes it so run lenses refresh in place.
			if (change.Key is "worktree.setupCommand" or Weavie.Core.Configuration.TestSettings.Profile) {
				_suggestions?.Evaluate();
			}

			if (change.Key == Weavie.Core.Configuration.TestSettings.Profile) {
				PushTestProfileToWeb();
			}
		};
		_settings.SettingChanged += _onSettingChanged;

		// A hand-edit that breaks settings.toml is otherwise silent (the parse error only reaches the console):
		// surface it where the user is, and clear it (same toast key) once the file parses cleanly again.
		_onSettingsMalformedChanged = NotifySettingsMalformed;
		_settings.MalformedChanged += _onSettingsMalformedChanged;

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

		// A binding to a typo'd/unknown command id is otherwise dropped silently (console only): name it so the
		// user learns why their key does nothing.
		_onUnknownKeybindingCommands = NotifyUnknownKeybindingCommands;
		_keybindings.UnknownCommandsChanged += _onUnknownKeybindingCommands;

		// A parse error in keybindings.json keeps the last-good bindings (it no longer wipes them to defaults):
		// surface that the file is being ignored, and clear it (same toast key) once it parses cleanly again.
		_onKeybindingsMalformedChanged = NotifyKeybindingsMalformed;
		_keybindings.MalformedChanged += _onKeybindingsMalformedChanged;

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

		// Recent files: record a visit whenever the primary session's active file changes. Primary-only (the
		// recents track the workspace's own checkout, like the editor session); dies with the core, like _layout.
		if (_primarySession is { } primary) {
			primary.Editor.Changed += RecordRecentFile;
		}
	}

	// Re-pushes the resolved theme (settings + overrides) so the web applies it live.
	private void PushThemeToWeb() => _bridge.PostToWeb(ThemeJson.Build(_settings, _themeOverrides, "theme", Log));

	// Surfaces (or clears) the malformed-settings toast. Keyed so the "reloaded" info replaces the lingering
	// error in place once the file parses again. Called on the live transition and once on the page's `ready`.
	private void NotifySettingsMalformed(bool malformed) {
		if (malformed) {
			Notify("error", $"Your settings file ({_settings.FilePath}) has errors and is being ignored until you fix it.", "settings-malformed");
		} else {
			Notify("info", "Settings reloaded — your settings.toml is active again.", "settings-malformed");
		}
	}

	// Surfaces a warning naming the keybindings.json command ids that match no command (their bindings are
	// dropped). Empty ⇒ the file is clean now: no-op (the prior warn auto-dismisses). Called on the live
	// change and once on the page's `ready`.
	private void NotifyUnknownKeybindingCommands(IReadOnlyList<string> ids) {
		if (ids.Count == 0) {
			return;
		}

		string list = string.Join(", ", ids.Select(id => $"'{id}'"));
		string verb = ids.Count == 1 ? "that binding was" : "those bindings were";
		Notify("warn", $"keybindings.json references unknown command {list} — {verb} ignored.", "keybindings-unknown");
	}

	// Surfaces (or clears) the malformed-keybindings toast. Keyed so the "reloaded" info replaces the lingering
	// error in place once the file parses again. Called on the live transition and once on the page's `ready`.
	private void NotifyKeybindingsMalformed(bool malformed) {
		if (malformed) {
			Notify("error", $"Your keybindings file ({_keybindings.FilePath}) has errors — your custom bindings are kept, but edits are ignored until you fix it.", "keybindings-malformed");
		} else {
			Notify("info", "Keybindings reloaded — your keybindings.json is active again.", "keybindings-malformed");
		}
	}

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

		if (_onUnknownKeybindingCommands is not null) {
			_keybindings.UnknownCommandsChanged -= _onUnknownKeybindingCommands;
			_onUnknownKeybindingCommands = null;
		}

		if (_onKeybindingsMalformedChanged is not null) {
			_keybindings.MalformedChanged -= _onKeybindingsMalformedChanged;
			_onKeybindingsMalformedChanged = null;
		}

		_shellSettingSubscription?.Dispose();
		_shellSettingSubscription = null;
		if (_onSettingChanged is not null) {
			_settings.SettingChanged -= _onSettingChanged;
			_onSettingChanged = null;
		}

		if (_onSettingsMalformedChanged is not null) {
			_settings.MalformedChanged -= _onSettingsMalformedChanged;
			_onSettingsMalformedChanged = null;
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
		_drainTick?.Cancel(); // ends a pending update drain's re-sample loop
		_sessionStore.Flush(); // persist the latest shell terminal size for the next launch's pre-spawn seed

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
