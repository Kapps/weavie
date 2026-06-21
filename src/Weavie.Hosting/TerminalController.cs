using System.Text;
using Weavie.Core.Configuration;
using Weavie.Core.Hooks;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>
/// Ties one real PTY to an xterm.js pane over the bridge. Each controller drives a single <em>session</em>:
/// <c>"claude"</c> launches the interactive <c>claude</c> TUI (with <c>ANTHROPIC_API_KEY</c> stripped so
/// billing stays on the user's subscription — interactive CLI = full plan, never <c>-p</c>/SDK);
/// <c>"shell"</c> launches the shell named by the <c>terminal.shell</c> setting. The OS-specific half
/// (which PTY backend, how to launch claude/the shell) is injected as an <see cref="IPtyLauncher"/>, so this
/// controller is identical on every host. The child runs under a <see cref="ProcessSupervisor"/> with
/// <see cref="RestartPolicy.Always"/>: a pane is a permanent fixture, so any exit (clean or crash) relaunches
/// it — only the crash-loop breaker leaves a stopped pane. The session id tags every
/// <c>term-output</c>/<c>term-exit</c> message so the page routes it to the matching pane. Only the claude
/// session optionally tees raw PTY bytes to WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP handshake).
/// </summary>
public sealed class TerminalController : IDisposable {
	private static readonly IReadOnlyList<string> NoSessionArgs = [];
	private readonly IHostBridge _bridge;
	private readonly string _session;
	private readonly SettingsStore _settings;
	private readonly IPtyLauncher _launcher;
	private readonly Lock _gate = new();
	private readonly ProcessSupervisor _supervisor;
	private ITerminal? _terminal;
	private FileStream? _ptyLog;
	private int _columns = 80;
	private int _rows = 24;
	// Page messages (restart/crash notices + exit) produced while this session is muted (OutputActive false). A
	// muted session shares the "claude"/"shell" tag with the visible one, so posting now would paint into the
	// WRONG pane — but dropping would lose a background session's crash notice (a dead session has no TUI to
	// repaint it). So buffer here and replay in OnReady when the user switches to this session. Guarded by _gate.
	private readonly List<string> _pendingMessages = [];
	// Per-launch claude startup detection (claude session only): watches early output to confirm the launch
	// came up (→ MarkStarted) and, on exit, decides how to heal a launch that died at startup (a dead/poison
	// session id), keeping ClaudeSessionStore honest. Set each managed launch; null for an unmanaged one.
	private ClaudeStartupWatcher? _startupWatcher;
	// Shell-only: the on-disk scrollback log this pane's output is tee'd to, for replay on (re)attach and faded
	// history on resume. Null = not configured (the claude pane, or persistence disabled). Created when
	// ScrollbackLogPath is set; unlike _ptyLog it SURVIVES process restarts (so a restart shows prior output
	// faded), and is closed only when the controller is disposed.
	private ScrollbackLog? _scrollback;

	/// <summary>
	/// Creates a controller that streams PTY output to (and input from) the given bridge, resolving its
	/// shell/claude/workspace from <paramref name="settings"/> and spawning the child through
	/// <paramref name="launcher"/>. <paramref name="session"/> is the pane this controller feeds:
	/// <c>"claude"</c> or <c>"shell"</c>.
	/// </summary>
	public TerminalController(IHostBridge bridge, string session, SettingsStore settings, IPtyLauncher launcher) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentException.ThrowIfNullOrEmpty(session);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(launcher);
		_bridge = bridge;
		_session = session;
		_settings = settings;
		_launcher = launcher;
		Workspace = settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_supervisor = new ProcessSupervisor(
			$"terminal:{session}",
			StartTerminal,
			StopTerminal,
			new SupervisionOptions { Policy = RestartPolicy.Always },
			LogSupervisor,
			clock: null);
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>Extra environment to inject into the spawned claude (used by the MCP wiring).</summary>
	public IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>The directory claude runs in (and the IDE workspace). Defaults to the <c>workspace</c> setting.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// When set (shell session only), the file this pane's output is persisted to so a reattaching client
	/// replays a coherent screen and a resumed shell shows its prior output faded. Setting it opens the log,
	/// capped by the <c>terminal.persistScrollbackKb</c> setting (0 disables). Left null for the claude pane,
	/// which resumes via <c>--resume</c> and repaints itself.
	/// </summary>
	public string? ScrollbackLogPath {
		get;
		set {
			field = value;
			EnsureScrollbackLog();
		}
	}

	/// <summary>
	/// Path to a Claude Code MCP config file passed as <c>--mcp-config</c> when launching claude, so the
	/// capability registry server's tools (<c>mcp__weavie__*</c>) are available to the model. Only the
	/// claude session uses it.
	/// </summary>
	public string? McpConfigPath { get; set; }

	/// <summary>
	/// Path to a Claude Code settings file passed as <c>--settings</c> when launching claude — the hooks
	/// block that routes its tool calls to Weavie's hook relay. Only the claude session uses it.
	/// </summary>
	public string? SettingsFilePath { get; set; }

	/// <summary>
	/// Path to a text file passed as <c>--append-system-prompt-file</c> when launching claude — the
	/// embedded-claude guidance that points it at the <c>mcp__weavie__*</c> tools for live app state. Only
	/// the claude session uses it.
	/// </summary>
	public string? SystemPromptFilePath { get; set; }

	/// <summary>
	/// When set (claude session only), the store that assigns this session's <see cref="Workspace"/> a stable
	/// Claude session id so the spawned claude resumes its previous conversation across launches/restarts
	/// (<c>--resume</c>) instead of cold-starting — created fresh the first time with <c>--session-id</c>.
	/// Honored only while the <c>claude.resumeSession</c> setting is on. Null disables the feature.
	/// </summary>
	public ClaudeSessionStore? ClaudeSessions { get; set; }

	/// <summary>
	/// Raised on every supervisor transition for this session's process (start, crash, restart, give-up),
	/// so a per-session status indicator can map it (crash / crash-loop → Error, post-crash restart →
	/// Starting). Fires on the supervisor's thread; handlers must not block.
	/// </summary>
	public event Action<SupervisorStateChanged>? SupervisorChanged;

	/// <summary>
	/// When false, output from this PTY is not posted to the page — the child keeps running (so a background
	/// session's claude stays live), its bytes just aren't shown. The session switcher sets this per session
	/// so only the active session feeds the page's xterm. Defaults true (single-session = always shown).
	/// </summary>
	public bool OutputActive { get; set; } = true;

	/// <summary>
	/// Handles the page's <c>term-ready</c> for this pane. If the child isn't running yet (first mount,
	/// after a <see cref="Restart"/>, or a brand-new session) it launches it in a PTY sized to the given
	/// columns and rows, and the child paints itself. If the child is <em>already</em> live — a session
	/// switch re-announced readiness after the page reset the xterm — it instead nudges the PTY size (one row
	/// shorter, then back) to force the running TUI to redraw into the now-blank pane; otherwise the pane
	/// stays blank until the user resizes. The decision and the nudge happen under the same lock, so the live
	/// child can't slip away between them; the start (which must not hold the gate — the supervisor's start
	/// callback takes it) runs after the lock only on the not-running branch.
	/// </summary>
	public void OnReady(int columns, int rows) {
		bool start;
		List<string> replay = [];
		lock (_gate) {
			_columns = columns;
			_rows = rows;
			if (_terminal is null) {
				start = true;
			} else {
				start = false;
				_terminal.Resize(_columns, Math.Max(1, _rows - 1));
				_terminal.Resize(_columns, _rows);
			}

			// Notices buffered while this session was muted (see PostOrBuffer). If we're about to (re)spawn they
			// describe the prior, now-superseded instance — drop them. Otherwise the session is live or died in
			// place: replay so a crash/restart the user missed shows in THIS pane, now that the page has reset
			// the xterm and re-emitted term-ready.
			if (_pendingMessages.Count > 0) {
				if (!start) {
					replay.AddRange(_pendingMessages);
				}

				_pendingMessages.Clear();
			}
		}

		// Replay the persisted scrollback (shell only) BEFORE (re)starting, so faded history + the current
		// process's reconstructed screen paint above any live output the new child emits. File I/O stays
		// outside _gate. (BuildReplay is empty for the claude pane, an empty log, or persistence off.)
		byte[] scrollback = _scrollback?.BuildReplay() ?? [];
		if (scrollback.Length > 0) {
			_bridge.PostToWeb(TermOutputJson(scrollback));
		}

		if (start) {
			_supervisor.Start();
		}

		foreach (string message in replay) {
			_bridge.PostToWeb(message);
		}
	}

	/// <summary>
	/// Starts the child at the default/cached size if it isn't running yet, WITHOUT the page binding to this
	/// pane — used to bring a session's backend up in the background (a "load, don't open"). When the page later
	/// binds, <see cref="OnReady"/> finds a live child and nudges it to the real pane size. No-op if running.
	/// </summary>
	public void EnsureStarted() {
		bool start;
		lock (_gate) {
			start = _terminal is null;
		}

		if (start) {
			_supervisor.Start();
		}
	}

	/// <summary>
	/// Intentionally tears down the running child (no auto-restart) and asks the page to reset this pane
	/// (which re-emits <c>term-ready</c> → <see cref="OnReady"/>), so a changed shell takes effect live.
	/// </summary>
	public void Restart() {
		_supervisor.Stop();
		Console.WriteLine($"[weavie] terminal[{_session}] restarting (setting changed)");
		Console.Out.Flush();
		// respawn=true: the child was torn down and will relaunch, re-establishing its modes from scratch,
		// so the page does a full reset (clean slate). The live-child switch path uses respawn=false instead.
		_bridge.PostToWeb($"{{\"type\":\"term-reset\",\"session\":\"{_session}\",\"respawn\":true}}");
	}

	/// <summary>Opens the scrollback log for the configured path once, honoring the size-cap setting (0 = disabled).</summary>
	private void EnsureScrollbackLog() {
		if (ScrollbackLogPath is null || _scrollback is not null) {
			return;
		}

		long kb = _settings.GetInt("terminal.persistScrollbackKb", 256);
		if (kb > 0) {
			int capBytes = (int)Math.Min(kb, int.MaxValue / 1024) * 1024;
			_scrollback = new ScrollbackLog(ScrollbackLogPath, capBytes);
		}
	}

	/// <summary>Spawns a fresh PTY child at the cached size; the supervisor calls this on first start and each restart.</summary>
	private void StartTerminal(int attempt) {
		bool isClaude = _session == "claude";
		string workspace = Workspace;
		lock (_gate) {
			_terminal?.Dispose();

			// Only the claude session tees to WEAVIE_PTY_LOG: both sessions sharing one path would
			// clash on the exclusive FileStream, and the log exists for the IDE-MCP handshake anyway.
			_ptyLog?.Dispose();
			string? logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
			_ptyLog = isClaude && !string.IsNullOrEmpty(logPath)
				? new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)
				: null;

			// Resolve this claude launch's managed session id (if any), then watch it: confirm it comes up (so the
			// next launch can --resume), and on exit heal a launch that died at startup so a dead/poison id can't
			// crash-loop the pane (see OnTerminalExited). Unmanaged launches (shell, or resume off) skip all this.
			var managedLaunch = isClaude ? ResolveClaudeLaunch() : null;
			_startupWatcher = managedLaunch is { } resolved ? new ClaudeStartupWatcher(resuming: resolved.Resume) : null;
			var sessionArgs = managedLaunch is { } managed
				? (IReadOnlyList<string>)[managed.Resume ? "--resume" : "--session-id", managed.SessionId]
				: NoSessionArgs;

			var launch = _launcher.Resolve(new PtyLaunchRequest {
				IsClaude = isClaude,
				Settings = _settings,
				McpConfigPath = McpConfigPath,
				SettingsFilePath = SettingsFilePath,
				SystemPromptFilePath = SystemPromptFilePath,
				ExtraEnvironment = ExtraEnvironment,
				ClaudeSessionArguments = sessionArgs,
			});

			var terminal = _launcher.CreateTerminal();
			terminal.Output += OnOutput;
			terminal.Exited += OnTerminalExited;
			terminal.Start(new TerminalStartInfo {
				Command = launch.Command,
				Arguments = launch.Arguments,
				WorkingDirectory = workspace,
				RemoveEnvironment = launch.RemoveEnvironment,
				Environment = launch.Environment,
				Columns = _columns,
				Rows = _rows,
			});
			_terminal = terminal;
			// Everything logged before now belongs to the previous process: mark the boundary so a replay
			// renders it faded and this new process's output live below it.
			_scrollback?.MarkBoundary();
			Console.WriteLine($"[weavie] terminal[{_session}] started (attempt {attempt}): {launch.Command} {string.Join(' ', launch.Arguments)} in {workspace} ({_columns}x{_rows})");
			Console.Out.Flush();
		}

		if (attempt > 0) {
			PostNotice($"\r\n[weavie] {_session} exited - restarting...\r\n");
		}
	}

	/// <summary>
	/// Keeps <see cref="ClaudeSessions"/> aligned with the conversation claude is ACTUALLY in, observed off the
	/// hook stream — so <c>--resume</c> tracks reality across a <c>/clear</c>. A <c>/clear</c> (SessionStart with
	/// <c>source=clear</c>) abandons the tracked id, so quitting right after a clear cold-starts fresh next launch
	/// instead of resuming the now-stale transcript; the next real user message (UserPromptSubmit) then adopts
	/// whatever id claude settled on, so a cleared-then-used session resumes its new conversation. No-op for the
	/// shell pane (no store), when <c>claude.resumeSession</c> is off, or for any other event. Runs on the hook
	/// accept-loop thread; the store is thread-safe.
	/// </summary>
	public void ObserveHook(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (ClaudeSessions is not { } store || !_settings.GetBool("claude.resumeSession", fallback: true)) {
			return;
		}

		switch (request.Event) {
			case HookEventKind.SessionStart when request.Source == "clear":
				store.Clear(Workspace);
				break;
			case HookEventKind.UserPromptSubmit when !string.IsNullOrEmpty(request.SessionId):
				store.Adopt(Workspace, request.SessionId);
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Resolves this directory's managed Claude launch — the stable session id and whether to <c>--resume</c> it
	/// or create it with <c>--session-id</c> (the caller turns this into the flag pair). Null when resume is
	/// unconfigured (<see cref="ClaudeSessions"/> null) or turned off via <c>claude.resumeSession</c> (claude
	/// then picks its own id). The resume <em>policy</em> lives in <see cref="ClaudeSessionStore"/>, shared
	/// across hosts; each <see cref="IPtyLauncher"/> just formats the resulting args for its OS.
	/// </summary>
	private ClaudeLaunch? ResolveClaudeLaunch() {
		if (ClaudeSessions is not { } store || !_settings.GetBool("claude.resumeSession", fallback: true)) {
			return null;
		}

		return store.Resolve(Workspace);
	}

	/// <summary>Tears down the current PTY child and its optional log; the supervisor calls this on stop/dispose.</summary>
	private void StopTerminal() {
		// Detach under the gate, then dispose OUTSIDE it. Disposing a ConPTY now blocks until its child has
		// actually exited (so a worktree delete that follows teardown can't race a still-open handle); during
		// that wait the child's final output can fire OnOutput → ObserveClaudeStartup, which itself takes _gate.
		// Holding the gate across Dispose would deadlock the two — detach-then-dispose tears down fully without it.
		ITerminal? terminal;
		FileStream? ptyLog;
		lock (_gate) {
			terminal = _terminal;
			_terminal = null;
			ptyLog = _ptyLog;
			_ptyLog = null;
		}

		terminal?.Dispose();
		ptyLog?.Dispose();
	}

	/// <summary>Maps supervisor state to pane UI: a real exit the policy won't relaunch, or the crash-loop give-up.</summary>
	private void OnSupervisorStateChanged(SupervisorStateChanged change) {
		// Forward every transition for status tracking (the session status machine maps it); the switch
		// below only drives the pane's own exit/crash notices.
		SupervisorChanged?.Invoke(change);
		switch (change.State) {
			case SupervisorState.Idle when change.ExitCode is int exitedCode:
				PostExit(exitedCode);
				break;
			case SupervisorState.Failed when change.ExitCode is int failedCode:
				PostNotice($"\r\n[weavie] {_session} crashed repeatedly - stopped.\r\n");
				PostExit(failedCode);
				break;
			default:
				break;
		}
	}

	/// <summary>Writes raw input bytes (keystrokes from xterm.js) to the PTY.</summary>
	public void Write(byte[] data) {
		lock (_gate) {
			_terminal?.Write(data);
		}
	}

	/// <summary>Resizes the PTY to the given column/row count (and remembers them for restarts).</summary>
	public void Resize(int columns, int rows) {
		lock (_gate) {
			_columns = columns;
			_rows = rows;
			_terminal?.Resize(columns, rows);
		}
	}

	private void OnOutput(byte[] data) {
		_ptyLog?.Write(data, 0, data.Length);
		_ptyLog?.Flush();
		// Persist to the scrollback log BEFORE the OutputActive gate, so a background (muted) session's output
		// is still captured for replay when the user later switches to it.
		_scrollback?.Append(data);

		// Drive the startup watcher BEFORE the OutputActive gate: a background (muted) claude must still confirm
		// it came up (and self-heal on a failed resume), even though its bytes aren't painted to the page.
		ObserveClaudeStartup(data);

		if (!OutputActive) {
			return;
		}

		_bridge.PostToWeb(TermOutputJson(data));
	}

	/// <summary>The <c>term-output</c> bridge message carrying <paramref name="data"/> base64-encoded for this pane.</summary>
	private string TermOutputJson(ReadOnlySpan<byte> data) =>
		$"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{Convert.ToBase64String(data)}\"}}";

	/// <summary>
	/// Feeds claude's early output to the per-launch <see cref="ClaudeStartupWatcher"/>; the moment it has streamed
	/// enough to be confirmed up, marks the session id started so the <em>next</em> launch resumes it. Failure is
	/// not decided here (a startup error is indistinguishable from healthy output by content) but on exit — see
	/// <see cref="OnTerminalExited"/>.
	/// </summary>
	private void ObserveClaudeStartup(byte[] data) {
		if (ClaudeSessions is not { } store) {
			return;
		}

		bool justConfirmed;
		lock (_gate) {
			// Only decode while a managed launch is still unconfirmed; once up, output is irrelevant here.
			if (_startupWatcher is not { Confirmed: false } watcher) {
				return;
			}

			justConfirmed = watcher.Observe(Encoding.UTF8.GetString(data));
		}

		if (justConfirmed) {
			store.MarkStarted(Workspace);
		}
	}

	/// <summary>
	/// The PTY child exited. For a managed claude launch that died before confirming it came up — a crash at
	/// startup, the signature of a dead/poison session id rather than a healthy run the user quit — this heals the
	/// session store so the supervisor's relaunch recovers instead of crash-looping: a failed <c>--resume</c>
	/// re-creates the same id, a failed <c>--session-id</c> forgets the id so the next launch mints a fresh one and
	/// cold-starts clean. An intentional stop (the supervisor has already left <see cref="SupervisorState.Running"/>
	/// before tearing the child down) and a clean exit are left alone. The supervisor is always notified last so it
	/// applies its restart policy regardless.
	/// </summary>
	private void OnTerminalExited(int code) {
		try {
			// A deliberate stop (Stop/Dispose/Restart) flips the supervisor out of Running before killing the
			// child, so a non-Running state here means the exit was intentional — never a startup failure to heal.
			bool unexpected = _supervisor.State == SupervisorState.Running;
			ClaudeStartupWatcher? watcher;
			lock (_gate) {
				watcher = _startupWatcher;
				_startupWatcher = null;
			}

			if (unexpected && ClaudeSessions is { } store && watcher is not null) {
				switch (watcher.OnExit(code)) {
					case ClaudeStartupRecovery.RecreateSameId:
						store.MarkResumeFailed(Workspace);
						break;
					case ClaudeStartupRecovery.ForgetId:
						store.Forget(Workspace);
						break;
					default:
						break; // None: came up, or a clean exit — leave the store as-is
				}
			}
		} finally {
			_supervisor.NotifyExited(code);
		}
	}

	private void PostExit(int code) {
		// Host log is unconditional; the page paint is buffered-or-posted by OutputActive (see PostOrBuffer).
		Console.WriteLine($"[weavie] terminal[{_session}] child exited: {code}");
		Console.Out.Flush();
		PostOrBuffer($"{{\"type\":\"term-exit\",\"session\":\"{_session}\",\"code\":{code}}}");
	}

	private void PostNotice(string text) {
		string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
		PostOrBuffer($"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{base64}\"}}");
	}

	/// <summary>
	/// Posts a page message now if this session is the visible one, else buffers it for replay when the user next
	/// switches here (<see cref="OnReady"/>). Both claude panes share the "claude" tag, so a muted session must
	/// not post into the foreground pane — but its restart/crash notices must not be lost either, so they wait
	/// here rather than being dropped.
	/// </summary>
	private void PostOrBuffer(string message) {
		lock (_gate) {
			if (!OutputActive) {
				_pendingMessages.Add(message);
				return;
			}
		}

		_bridge.PostToWeb(message);
	}

	private static void LogSupervisor(SupervisorLogEntry entry) {
		Console.WriteLine($"[weavie] supervisor[{entry.Name}] {entry.Level}: {entry.Message}");
		Console.Out.Flush();
	}

	/// <summary>Tears down the supervised PTY child process and closes the optional PTY debug log + scrollback log.</summary>
	public void Dispose() {
		_supervisor.Dispose();
		lock (_gate) {
			_ptyLog?.Dispose();
			_ptyLog = null;
			// Close the handle but leave the on-disk content, so a later resume of this worktree can replay it faded.
			_scrollback?.Dispose();
			_scrollback = null;
		}
	}
}
