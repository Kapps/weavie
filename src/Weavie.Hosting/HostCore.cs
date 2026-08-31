using System.Reflection;
using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Corrections;
using Weavie.Core.Diagnostics;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Core.Remote;
using Weavie.Core.Search;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
using Weavie.Core.Suggestions;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;
using Weavie.Hosting.Web;

namespace Weavie.Hosting;

/// <summary>
/// The shared, platform-agnostic host core every platform shell drives. Owns one workspace's Core graph and
/// session set, routes exact-addressed messages to their owning host/session buses, and pushes state over the bridge;
/// everything OS-specific is reached through an injected <see cref="IHostPlatform"/>. Split into three partials:
/// this file (lifecycle), <c>HostCore.WebBridge.cs</c> (message dispatch), and <c>HostCore.Sessions.cs</c>.
/// </summary>
public sealed partial class HostCore : IAsyncDisposable {
	private readonly IHostPlatform _platform;
	private readonly TimeProvider _drainTime;
	private readonly HostRuntimeInfo _runtime;
	private readonly IWebTransportHub _bridge;
	private readonly HostMessageRouter _messages;
	private readonly MessageIngress _messageIngress;
	private readonly string _hostIncarnation = Guid.NewGuid().ToString("n");
	private readonly IUiDispatcher _ui;
	private readonly SettingsStore _settings;
	private readonly CommandRegistry _commandRegistry;
	private readonly CommandDispatcher _clientCommands;
	private readonly SuggestionRegistry _suggestionRegistry;
	private readonly KeybindingStore _keybindings;
	private readonly ThemeOverridesStore _themeOverrides;
	// App-global Claude-session-id map (keyed by cwd); each session resumes its own worktree's conversation.
	private readonly AgentProviderRegistry _agentProviders;
	private readonly IAcpAgentCatalog _acpAgents;
	private readonly IInferenceService _inference;
	// App-global remote-agent registry; included in hello and re-pushed on change (the web owns the
	// connections, this owns persistence — see remote-agents.ts).
	private readonly RemoteAgentStore _remoteAgents;
	// App-global session-rail UI state (last-used backend + promoted remote sessions); same push pattern.
	private readonly RailStateStore _railState;
	// App-global find-in-files UI state (match options, include/exclude globs, recent terms); same push pattern.
	private readonly SearchStateStore _searchState;
	// App-global captured console output (stdout/stderr teed into a bounded ring), served by the in-app log viewer.
	private readonly LogBuffer _logBuffer;
	// Where a prior run's unhandled-crash report lives / rotates to; test hosts point these at a private temp
	// dir so concurrent test processes never race the real path (or each other) — see HostServices.
	private readonly string _lastCrashFile;
	private readonly string _previousCrashFile;
	// Lists open PRs for the Open-PR flow (GitHub by default; a static stub under the headless harness).
	private readonly Weavie.Core.Review.IPullRequestProvider _pullRequests;
	// Loads/posts a PR's review comments (same GitHub client, or the harness stub).
	private readonly Weavie.Core.Review.IReviewCommentStore _reviewComments;
	// The source system (Notion personal-access-token validate + fetch); see HostCore.Sources.cs.
	private readonly Weavie.Core.Sources.ISourceConnector _sources;
	private readonly LayoutStore _layout;
	// Per-workspace session overlay, so reopen restores each slot's runtime and editor state.
	private readonly SessionStore _sessionStore;
	private readonly RecentFilesStore _recentFiles;
	private readonly CorrectionCorpus _corrections;
	private readonly WorkspaceMediaRoutes _mediaRoutes = new();
	private readonly WorkspaceHttpServer _http;
	// Every loaded or dormant session slot. Which one a page displays is client state and never appears here.
	private SessionManager? _sessions;
	private string _workspaceSessionLabel = string.Empty;
	// Empty unless the startup shell probe failed; surfaced at hello, where the user can actually see it.
	private string _environmentImportFailure = string.Empty;
	private WorktreeManager? _worktrees;
	private ShellWorktreeProvisioner? _worktreeProvisioner;
	// StartAsync is idempotent: the Windows shell kicks it off early to overlap the slow WebView2 environment
	// creation, and the web launcher awaits it again — both join this one run.
	private readonly object _startGate = new();
	private readonly SemaphoreSlim _sessionLifecycle = new(1, 1);
	private Task? _startTask;
	private Task? _disposeTask;

	// Drives frameless title-bar controls when the platform exposes an IShellWindow. File-menu actions have a
	// separate required adapter because native-frame hosts can still render the web app bar.
	private ShellController? _shell;
	private ShellMenuController? _shellMenu;
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
	private Action? _onSearchStateChanged;
	private Action? _onAgentProvidersChanged;
	private Action? _onRecentsChanged;
	private IDisposable? _shellSettingSubscription;

	/// <summary>
	/// Builds only the cheap per-workspace stores so the shell can read the saved window
	/// geometry before creating its window; the heavy graph is built by <see cref="StartAsync"/>.
	/// </summary>
	public HostCore(
		IHostPlatform platform,
		HostServices services,
		string workspaceRoot,
		WorkspaceHttpServerOptions httpOptions,
		IWorkspaceWebSocketBridge httpBridge)
		: this(platform, services, workspaceRoot, httpOptions, httpBridge, TimeProvider.System) {
	}

	internal HostCore(
		IHostPlatform platform,
		HostServices services,
		string workspaceRoot,
		WorkspaceHttpServerOptions httpOptions,
		IWorkspaceWebSocketBridge httpBridge,
		TimeProvider drainTime) {
		ArgumentNullException.ThrowIfNull(platform);
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(httpOptions);
		ArgumentNullException.ThrowIfNull(httpBridge);
		ArgumentNullException.ThrowIfNull(drainTime);
		_platform = platform;
		_drainTime = drainTime;
		// The build a managed worker actually loaded (its own versions/<build>/ path), or the dev version — surfaced
		// to the embedded claude so it knows whether it's a remote worker and on which build. See HostRuntimeInfo.
		_runtime = HostRuntimeInfo.Resolve(platform.Transport, AppContext.BaseDirectory, BuildNumber);
		_bridge = platform.Bridge;
		_ui = platform.Dispatcher;
		_settings = services.Settings;
		var messagePolicy = new MessageExecutionPolicy(
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(_settings.RequireInt(MessageSettings.OperationDeadlineSeconds)));
		_messages = new HostMessageRouter(_bridge, _ui, Log, messagePolicy, TimeProvider.System);
		_messageIngress = new MessageIngress(
			_ui,
			_messages.RouteAsync,
			_messages.Disconnect,
			_messages.Diagnostics);
		_commandRegistry = services.CommandRegistry;
		_clientCommands = new CommandDispatcher(_commandRegistry);
		_clientCommands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			_ui.Post(_platform.ToggleWindow);
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		ThemeCommands.RegisterHandlers(_clientCommands, _settings, services.ThemeOverrides, VsixPicker);
		FontCommands.RegisterHandlers(_clientCommands, _settings);
		GitBlameCommands.RegisterHandlers(_clientCommands, _settings);
		InferenceCommands.RegisterHandlers(_clientCommands, _settings);
		_suggestionRegistry = services.SuggestionRegistry;
		_keybindings = services.Keybindings;
		_themeOverrides = services.ThemeOverrides;
		_agentProviders = services.AgentProviders;
		_acpAgents = services.AcpAgents;
		_inference = services.Inference;
		_remoteAgents = services.RemoteAgents;
		_railState = services.RailState;
		_searchState = services.SearchState;
		_logBuffer = services.LogBuffer;
		_lastCrashFile = services.LastCrashFile;
		_previousCrashFile = services.PreviousCrashFile;
		_pullRequests = services.PullRequests;
		_reviewComments = services.ReviewComments;
		_sources = services.Sources;
		WorkspaceRoot = workspaceRoot;
		Id = WorkspaceId.ForPath(workspaceRoot);

		// Back per-workspace settings (worktree.setupCommand, test.profile) from the workspace's out-of-repo overlay.
		// On single-workspace hosts the store gets one workspace; on Windows the shared store gets one per window.
		_settings.RegisterWorkspace(workspaceRoot);

		// Per-workspace layout and session catalog, keyed by the folder's path id.
		_layout = LayoutPanes.CreateStore(WeaviePaths.WorkspaceLayoutFile(Id));
		_sessionStore = new SessionStore(new LocalFileSystem(), WeaviePaths.WorkspaceSessionsFile(Id));
		_sessionStore.Log += Log;
		_recentFiles = new RecentFilesStore(new LocalFileSystem(), WeaviePaths.WorkspaceRecentFilesFile(Id));
		_recentFiles.Log += Log;
		// One correction ring per workspace, shared by every session/worktree: rules about how the agent codes
		// in this repo are repo-level. Its count gates the corrections.learn card, so changes re-evaluate.
		_corrections = new CorrectionCorpus(new LocalFileSystem(), WeaviePaths.WorkspaceCorrectionsFile(Id));
		_corrections.Log += Log;
		_corrections.Changed += () => _suggestions?.Evaluate();
		_http = new WorkspaceHttpServer(this, httpOptions, httpBridge, _mediaRoutes);
		WireHostMessages();
	}

	// The last file recorded as recent, so the active-editor stream (which re-fires on every cursor move within a
	// file) bumps frecency once per distinct file visit, not per move.
	private string? _lastRecentPath;

	/// <summary>
	/// Raised when the connection hello is built. A shell with a web-rendered title bar subscribes to push the
	/// initial native window state, which only it knows. UI thread.
	/// </summary>
	public event Action? Ready;

	/// <summary>This workspace's stable identity (path-derived).</summary>
	public WorkspaceId Id { get; }

	/// <summary>The absolute workspace root this core serves.</summary>
	public string WorkspaceRoot { get; }

	/// <summary>The shared HTTP origin serving this workspace's app and streamed resources.</summary>
	public string WorkspaceOrigin => _http.Origin;

	/// <summary>The authenticated workspace document served by the shared HTTP server.</summary>
	public string WorkspacePageUrl => _http.PageUrl;

	/// <summary>The native WebView document that establishes the workspace cookie before redirecting clean.</summary>
	public string WorkspaceNativePageUrl => _http.NativePageUrl;

	/// <summary>The token a browser submits once at the workspace connect page.</summary>
	public string WorkspaceAccessToken => _http.AccessToken;

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
	/// Builds the workspace's live backend: the session set (pre-existing worktrees
	/// reconciled into dormant chips), the title-bar controller, and the store reactions. Idempotent — the shell
	/// may kick it off early (to overlap WebView2 bring-up) and the web launcher awaits it again; both join one run.
	/// Call after the bridge is attached.
	/// </summary>
	public Task StartAsync() {
		lock (_startGate) {
			ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
			return _startTask ??= StartCoreAsync();
		}
	}

	private async Task StartCoreAsync() {
		_shellMenu = new ShellMenuController(_platform.MenuActions);
		await _http.StartAsync().ConfigureAwait(false);
		// Record any unhandled background-thread exception to a crash log (and stderr) before the runtime tears
		// down, so a hard exit leaves a trace instead of vanishing; surfaced as a toast on the next launch.
		CrashReporter.Install(line => Log($"[crash] {line}"), _lastCrashFile);

		// Any launch context can carry a truncated environment and a stingy open-file limit — a Finder .app or
		// desktop entry via launchd, a headless host under a supervisor. Raise the descriptor limit so a second
		// session can't exhaust it mid-switch, and import the login-shell environment so spawned children (LSP
		// servers, git) resolve as from a terminal. Both no-op on Windows and when nothing needs raising.
		PosixFileLimit.RaiseToHardLimit(line => Log($"[fd] {line}"));
		_environmentImportFailure =
			await LoginShellEnvironment.ImportOnceAsync(line => Log($"[env] {line}")).ConfigureAwait(false);

		_bridge.MessageReceived += OnWebMessage;
		_bridge.PeerDisconnected += OnWebPeerDisconnected;

		// One git probe shared by the rail label and the worktree manager (was two redundant is-repo calls).
		var (git, isRepo) = await ProbeGitAsync().ConfigureAwait(false);
		_workspaceSessionLabel = await ResolveWorkspaceSessionLabelAsync(git, isRepo).ConfigureAwait(false);

		// Frameless title-bar controls exist only when the platform exposes native window primitives. File-menu
		// actions use their separate required adapter, so a native-frame host can still render the web app bar.
		if (_platform.Window is { } window) {
			_shell = new ShellController(window);
		}

		// Reconcile checkouts first, then restore per-slot runtime/editor state and ensure the workspace checkout
		// has a convenient session when none was persisted for it.
		_worktrees = isRepo ? BuildWorktreeManager(git) : null;
		_sessions = new SessionManager(_worktrees);
		await ReconcileWorktreesOnOpenAsync().ConfigureAwait(false);
		RestoreSessionState();

		// Contextual suggestions: the manifest probe runs off the hot path; its state is pushed independently.
		InitSuggestions();

		WireReactions();
		_http.MarkReady();
	}

	/// <summary>Waits for this workspace server to be stopped (the Headless process lifetime).</summary>
	public Task WaitForShutdownAsync() => _http.WaitForShutdownAsync();

	/// <summary>
	/// The same-origin page bootstrap: resolved fonts, editor options, theme, command catalog, keybindings, and
	/// shell config. Call after <see cref="StartAsync"/>.
	/// </summary>
	public string BuildBootstrap() {
		return
			string.Concat(LiveSettingGroups.Select(g => $"window.{g.Global} = {g.Build(_settings)};"))
			+ $"window.__WEAVIE_AGENT__ = {BuildAgentDefaults()};"
			+ $"window.__WEAVIE_THEME__ = {ThemeJson.Build(_settings, _themeOverrides, Log)};"
			+ BuildTestProfileScript()
			+ $"window.__WEAVIE_COMMANDS__ = {_keybindings.BuildCommandsJson()};"
			+ $"window.__WEAVIE_KEYBINDINGS__ = {_keybindings.BuildKeybindingsJson()};"
			+ ShellProtocol.BuildConfigScript(_platform.ChromePlatform, _platform.TitleBar, WorkspaceLabel, _platform.Recents, BuildNumber);
	}

	internal string BuildCrossOriginBootstrap() =>
		$"window.__WEAVIE_RESOURCE_BASE__ = {JsonSerializer.Serialize(_http.TransportMediaBaseUrl)};"
		+ BuildBootstrap();

	// Live settings groups: each is injected pre-navigation as window.{Global} and re-pushed as its
	// event name when any of its Keys changes. One row per group — the bootstrap and the change handler
	// both iterate this table.
	private static readonly (IReadOnlyList<string> Keys, string EventName, string Global,
		Func<SettingsStore, string> Build)[] LiveSettingGroups = [
		(FontSettings.Keys, "fonts", "__WEAVIE_FONTS__", FontSettings.BuildJson),
		(NotificationSettings.Keys, "notification-prefs", "__WEAVIE_NOTIFICATIONS__", NotificationSettings.BuildJson),
		(EditorSettings.Keys, "editorOptions", "__WEAVIE_EDITOR_OPTIONS__", EditorSettings.BuildJson),
	];

	private string BuildAgentDefaults() => AgentSettings.BuildJson(
		_settings,
		[.. _agentProviders.Providers.Select(provider => provider.Info)]);

	/// <summary>The app's build identity (SemVer with the build number as patch, e.g. <c>0.1.247</c>), stamped at build time.</summary>
	public static string BuildNumber =>
		typeof(HostCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
		?? throw new InvalidOperationException("Weavie.Hosting has no AssemblyInformationalVersion — the build-stamp target did not run.");

	/// <summary>Pushes the native window's current state for title-bar chrome and attention delivery.</summary>
	public void PushWindowState(bool maximized, bool focused) =>
		_messages.Host.Feature("window").Publish("state", new { maximized, focused });

	/// <summary>
	/// Wires the live reactions to store changes: a changed shell reopens the terminal; font/editor/theme/
	/// keybinding/layout edits re-push their resolved values.
	/// </summary>
	private void WireReactions() {
		// A changed shell (ApplyMode.ReopensTerminal) reopens every loaded session's shell pane.
		_shellSettingSubscription = _settings.Subscribe(
			CoreSettings.TerminalShell,
			_ => _ui.Post(() => {
				foreach (var session in LoadedSessions()) {
					session.Shells.Restart();
				}
			}));

		// Live settings groups + theme: re-push the resolved values so the web applies them in place.
		// Broadcast marshals to the UI thread and the stores are thread-safe, so call it directly.
		_onSettingChanged = change => {
			foreach (var (keys, eventName, _, build) in LiveSettingGroups) {
				if (keys.Contains(change.Key)) {
					_messages.Host.Feature("settings").PublishJson(eventName, build(_settings));
				}
			}
			if (AgentSettings.Keys.Contains(change.Key)) {
				_messages.Host.Feature("settings").PublishJson("agent-defaults", BuildAgentDefaults());
			}

			if (ThemeSettings.Keys.Contains(change.Key)) {
				PushThemeToWeb();
			}

			if (change.Key is InferenceSettings.Enabled or InferenceSettings.AllowAutomatic
				&& AutomaticInferenceEnabled()) {
				ClearAutomaticInferenceOffer();
			}

			// Configuring the worktree setup command or the test profile can make the workspace-setup card vanish;
			// re-evaluate the suggestions. A changed test profile also re-pushes it so run lenses refresh in place.
			if (change.Key is CoreSettings.WorktreeSetupCommand or TestSettings.Profile) {
				_suggestions?.Evaluate();
			}

			if (change.Key == TestSettings.Profile) {
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
		_onKeybindingsChanged = () => _messages.Host.Feature("commands").PublishJson(
			"catalog",
			$"{{\"commands\":{_keybindings.BuildCommandsJson()},"
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
		// the registry so every page's rail + New Session location list stays in sync. Broadcast marshals itself.
		_onRemoteAgentsChanged = PushRemoteAgentsToWeb;
		_remoteAgents.Changed += _onRemoteAgentsChanged;

		// Session rail UI state (last-used backend + promoted remotes): same re-push-on-change as remote agents.
		_onRailStateChanged = PushRailStateToWeb;
		_railState.Changed += _onRailStateChanged;

		// Find-in-files UI state (options + globs + recent terms): same re-push-on-change.
		_onSearchStateChanged = PushSearchStateToWeb;
		_searchState.Changed += _onSearchStateChanged;

		// Recent workspaces are app-global: opening/pruning one in any window refreshes every existing File menu.
		_onRecentsChanged = PushRecentWorkspacesToWeb;
		_platform.RecentsChanged += _onRecentsChanged;

		_onAgentProvidersChanged = () =>
			_messages.Host.Feature("settings").PublishJson("agent-defaults", BuildAgentDefaults());
		_agentProviders.Changed += _onAgentProvidersChanged;

		// Layout: when the store changes (a reconciled web edit, or an MCP setLayout), push the canonical
		// document back so the web re-renders. Change events arrive off the UI thread.
		_layout.Changed += _ => _ui.Post(PushLayoutToWeb);

	}

	// Re-pushes the resolved theme (settings + overrides) so the web applies it live.
	private void PushThemeToWeb() =>
		_messages.Host.Feature("settings").PublishJson(
			"theme",
			ThemeJson.Build(_settings, _themeOverrides, Log));

	// Surfaces (or clears) the malformed-settings toast. Keyed so the "reloaded" info replaces the lingering
	// error in place once the file parses again. Called on the live transition and once during hello.
	private void NotifySettingsMalformed(bool malformed) {
		if (malformed) {
			Notify("error", $"Your settings file ({_settings.FilePath}) has errors and is being ignored until you fix it.", "settings-malformed");
		} else {
			Notify("info", "Settings reloaded — your settings.toml is active again.", "settings-malformed");
		}
	}

	// Surfaces a warning naming the keybindings.json command ids that match no command (their bindings are
	// dropped). Empty ⇒ the file is clean now: no-op (the prior warn auto-dismisses). Called on the live
	// change and once during hello.
	private void NotifyUnknownKeybindingCommands(IReadOnlyList<string> ids) {
		if (ids.Count == 0) {
			return;
		}

		string list = string.Join(", ", ids.Select(id => $"'{id}'"));
		string verb = ids.Count == 1 ? "that binding was" : "those bindings were";
		Notify("warn", $"keybindings.json references unknown command {list} — {verb} ignored.", "keybindings-unknown");
	}

	// Surfaces (or clears) the malformed-keybindings toast. Keyed so the "reloaded" info replaces the lingering
	// error in place once the file parses again. Called on the live transition and once during hello.
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
	public ValueTask DisposeAsync() {
		lock (_startGate) {
			return new ValueTask(_disposeTask ??= DisposeCoreAsync(_startTask));
		}
	}

	private async Task DisposeCoreAsync(Task? startTask) {
		var failures = new List<Exception>();
		void Attempt(Action action) {
			try {
				action();
			} catch (Exception ex) {
				failures.Add(ex);
			}
		}

		async Task AttemptAsync(Func<Task> action) {
			try {
				await action().ConfigureAwait(false);
			} catch (Exception ex) {
				failures.Add(ex);
			}
		}

		if (startTask is not null) {
			await AttemptAsync(() => startTask).ConfigureAwait(false);
		}

		Attempt(() => _bridge.MessageReceived -= OnWebMessage);
		Attempt(() => _bridge.PeerDisconnected -= OnWebPeerDisconnected);
		await AttemptAsync(() => _messageIngress.DisposeAsync().AsTask()).ConfigureAwait(false);
		await AttemptAsync(() => _messages.Host.QuiesceAsync()).ConfigureAwait(false);
		await AttemptAsync(DisposeSystemNotificationsAsync).ConfigureAwait(false);
		Attempt(DetachReactions);
		Attempt(() => _drainTick?.Cancel());
		Attempt(_sessionStore.Flush);

		var sessions = _sessions;
		_sessions = null;
		if (sessions is not null) {
			await AttemptAsync(() => sessions.DisposeAsync().AsTask()).ConfigureAwait(false);
		}

		await AttemptAsync(() => _messages.DisposeAsync().AsTask()).ConfigureAwait(false);
		await AttemptAsync(() => _http.DisposeAsync().AsTask()).ConfigureAwait(false);
		if (failures.Count > 0) {
			throw new AggregateException("One or more host shutdown operations failed.", failures);
		}
	}

	private void DetachReactions() {
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

		if (_onSearchStateChanged is not null) {
			_searchState.Changed -= _onSearchStateChanged;
			_onSearchStateChanged = null;
		}
		if (_onAgentProvidersChanged is not null) {
			_agentProviders.Changed -= _onAgentProvidersChanged;
			_onAgentProvidersChanged = null;
		}

		if (_onRecentsChanged is not null) {
			_platform.RecentsChanged -= _onRecentsChanged;
			_onRecentsChanged = null;
		}
	}
}
