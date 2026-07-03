using System.Text;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Hooks;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Weavie.Core.Terminal;

namespace Weavie.Hosting;

/// <summary>
/// Ties one real PTY to an xterm.js pane over the bridge. Each controller drives one <em>session</em>:
/// <c>"claude"</c> launches the interactive <c>claude</c> TUI (with <c>ANTHROPIC_API_KEY</c> stripped so
/// billing stays on the user's subscription — interactive CLI = full plan, never <c>-p</c>/SDK);
/// <c>"shell"</c> launches the <c>terminal.shell</c> setting's shell. The OS-specific half is an injected
/// <see cref="IPtyLauncher"/>, so the controller is identical on every host. The child runs under a
/// <see cref="ProcessSupervisor"/> with <see cref="RestartPolicy.Always"/> (a pane is a permanent fixture, so
/// any exit relaunches it; only the crash-loop breaker leaves it stopped). The session id tags every
/// <c>term-*</c> message so the page routes it to the matching pane.
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
	// The slot id pre-encoded as a JSON string value (see SlotId), written into every term message so the
	// page routes this pane's output to its own session's xterm — encoded once per bind, not per chunk.
	private JsonEncodedText _slotEncoded = JsonEncodedText.Encode("");
	// Per-launch claude startup detection (claude session only): watches early output so that, on exit, it can
	// tell a launch that came up from one that died at startup (a dead/poison session id) and heal
	// ClaudeSessionStore. Does NOT mark the session resumable (that's ObserveHook). Null for an unmanaged launch.
	private ClaudeStartupWatcher? _startupWatcher;
	// Shell-only: the on-disk scrollback log this pane's output is tee'd to, for replay on (re)attach and faded
	// history on resume. Unlike _ptyLog it survives process restarts and is closed only on dispose. Null = not
	// configured (the claude pane, or persistence disabled).
	private ScrollbackLog? _scrollback;
	// Shell-only: the directory the shell last reported via OSC 7, so a reopen relaunches there instead of the
	// workspace root. Null until reported (or for the claude pane, which always runs in the IDE workspace).
	private string? _reportedCwd;
	// Per-launch latched terminal state (alt screen, mouse modes, bracketed paste, title…), replayed to a client
	// that mounts onto the already-live child — the resize nudge redraws content but can't re-establish modes.
	// Replaced with a fresh instance per launch in StartTerminal, which is also the reset on restart.
	private TerminalModeTracker _modes = new();

	/// <summary>
	/// Creates a controller that streams PTY output to (and input from) <paramref name="bridge"/>, resolving its
	/// shell/claude/workspace from <paramref name="settings"/> and spawning the child through
	/// <paramref name="launcher"/>. <paramref name="session"/> is the pane it feeds: <c>"claude"</c> or <c>"shell"</c>.
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
	/// When set (shell session only), the file this pane's output is persisted to so a reattaching/resumed client
	/// replays a coherent screen (faded). Setting it opens the log, capped by <c>terminal.persistScrollbackKb</c>
	/// (0 disables). Null for the claude pane, which resumes via <c>--resume</c> and repaints itself.
	/// </summary>
	public string? ScrollbackLogPath {
		get;
		set {
			field = value;
			EnsureScrollbackLog();
		}
	}

	/// <summary>
	/// MCP config file passed as <c>--mcp-config</c> when launching claude, exposing the capability registry
	/// server's tools (<c>mcp__weavie__*</c>) to the model. Claude session only.
	/// </summary>
	public string? McpConfigPath { get; set; }

	/// <summary>
	/// Settings file passed as <c>--settings</c> when launching claude — the hooks block routing its tool calls
	/// to Weavie's hook relay. Claude session only.
	/// </summary>
	public string? SettingsFilePath { get; set; }

	/// <summary>
	/// Text file passed as <c>--append-system-prompt-file</c> when launching claude — the embedded-claude
	/// guidance pointing it at the <c>mcp__weavie__*</c> tools for live app state. Claude session only.
	/// </summary>
	public string? SystemPromptFilePath { get; set; }

	/// <summary>
	/// When set (claude session only), the store assigning this <see cref="Workspace"/> a stable Claude session id
	/// so the spawned claude <c>--resume</c>s its previous conversation across launches instead of cold-starting.
	/// Honored only while <c>claude.resumeSession</c> is on; null disables the feature.
	/// </summary>
	public ClaudeSessionStore? ClaudeSessions { get; set; }

	/// <summary>
	/// Locator for Claude's on-disk transcripts, wired alongside <see cref="ClaudeSessions"/> (claude session only),
	/// so a <c>--resume</c> is only launched when its conversation actually exists — otherwise the id is re-created
	/// fresh instead of resuming into "No conversation found". Null disables the check (the launch resumes as before).
	/// </summary>
	public IClaudeTranscripts? ClaudeTranscripts { get; set; }

	/// <summary>
	/// Raised on every supervisor transition for this session's process, so a per-session status indicator can map
	/// it (crash/crash-loop → Error, post-crash restart → Starting). Fires on the supervisor's thread; don't block.
	/// </summary>
	public event Action<SupervisorStateChanged>? SupervisorChanged;

	/// <summary>
	/// The rail slot id this pane belongs to, tagged onto every message so the page routes it to this session's
	/// own xterm. Every loaded session streams concurrently into its own (hidden) pane, so a switch is instant.
	/// Set when the session is bound to a slot; empty until then.
	/// </summary>
	public string SlotId {
		get;
		set {
			field = value;
			_slotEncoded = JsonEncodedText.Encode(value);
		}
	} = "";

	/// <summary>
	/// Handles the page's <c>term-ready</c> for this pane (a session's xterm mounting). If the child isn't running
	/// it launches it sized to the given columns/rows. If it's already live (a cold reattach), the fresh xterm has
	/// missed everything the child established at startup, so this replays the persisted scrollback (shell only),
	/// then the latched terminal modes (alt screen, mouse tracking, bracketed paste, title — without which a
	/// fullscreen TUI renders into the normal buffer and grows scrollback it never wanted), and only then nudges
	/// the PTY size (one row shorter, then back) to make the TUI redraw — into the now-correct buffer. The redraw
	/// bytes can't overtake the preamble: they only exist after the resize reaches the child and come back through
	/// the PTY read thread. The start runs after the lock (the supervisor's start callback takes the gate).
	/// </summary>
	public void OnReady(int columns, int rows) {
		bool start;
		byte[] restore = [];
		lock (_gate) {
			_columns = columns;
			_rows = rows;
			start = _terminal is null;
			if (!start) {
				restore = _modes.BuildRestore();
			}
		}

		// Replay persisted scrollback (shell only) before (re)starting, so faded history paints above the new
		// child's live output. File I/O stays outside _gate. (BuildReplay is empty for claude / no persistence.)
		byte[] scrollback = _scrollback?.BuildReplay() ?? [];
		if (scrollback.Length > 0) {
			_bridge.PostToWeb(TermOutputJson(scrollback));
		}

		if (start) {
			_supervisor.Start();
			return;
		}

		if (restore.Length > 0) {
			_bridge.PostToWeb(TermOutputJson(restore));
		}

		lock (_gate) {
			_terminal?.Resize(_columns, Math.Max(1, _rows - 1));
			_terminal?.Resize(_columns, _rows);
		}
	}

	/// <summary>
	/// Starts the child at the cached size without the page binding to this pane — brings a session's backend up
	/// in the background ("load, don't open"); <see cref="OnReady"/> later nudges it to the real pane size. No-op
	/// if running.
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
		// respawn=true: the child relaunches and re-establishes its modes, so the page does a full reset. The sole
		// term-reset caller — a session switch keeps each session's own live xterm and doesn't reset.
		_bridge.PostToWeb($"{{\"slot\":\"{_slotEncoded}\",\"type\":\"term-reset\",\"session\":\"{_session}\",\"respawn\":true}}");
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

	/// <summary>
	/// Records the working directory the shell child reported via OSC 7, so a reopen relaunches there. OSC 7 is
	/// untrusted terminal output, so the path is confined to this session's worktree — it can't point the
	/// relaunched shell at an arbitrary directory (a binary/DLL-planting or info-disclosure vector). Ignored for
	/// the claude pane (always the IDE workspace) and for a path outside the workspace or one that no longer exists.
	/// </summary>
	public void OnCwdReported(string cwd) {
		if (_session == "claude" || !BufferStore.IsWithinWorkspace(Workspace, cwd) || !Directory.Exists(cwd)) {
			return;
		}

		lock (_gate) {
			_reportedCwd = cwd;
		}
	}

	/// <summary>Spawns a fresh PTY child at the cached size; the supervisor calls this on first start and each restart.</summary>
	private void StartTerminal(int attempt) {
		bool isClaude = _session == "claude";
		lock (_gate) {
			// The shell relaunches in its last reported cwd (OSC 7) when it has one; claude always uses the workspace.
			string workspace = _reportedCwd ?? Workspace;
			_terminal?.Dispose();

			// Only the claude session tees to WEAVIE_PTY_LOG: both sessions sharing one path would
			// clash on the exclusive FileStream, and the log exists for the IDE-MCP handshake anyway.
			_ptyLog?.Dispose();
			string? logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
			_ptyLog = isClaude && !string.IsNullOrEmpty(logPath)
				? new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read)
				: null;

			// Resolve this claude launch's managed session id (if any) and watch it, so a launch that died at startup
			// (a dead/poison id) self-heals instead of crash-looping the pane (see OnTerminalExited). Resumability is
			// decided by ObserveHook, not here. Unmanaged launches (shell, or resume off) skip all this.
			var managedLaunch = isClaude ? ResolveClaudeLaunch() : null;
			_startupWatcher = managedLaunch is { } resolved ? new ClaudeStartupWatcher(resuming: resolved.Resume) : null;
			var modes = new TerminalModeTracker();
			_modes = modes;
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
			// The tracker is captured per launch (not read from the field) so a late chunk from a dying child can
			// never latch into the next launch's restore preamble.
			terminal.Output += data => OnOutput(data, modes);
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
	/// Keeps <see cref="ClaudeSessions"/> aligned with the conversation claude is actually in (off the hook
	/// stream), so <c>--resume</c> tracks reality across a <c>/clear</c>: a <c>/clear</c> abandons the tracked id
	/// (next launch cold-starts), and the next UserPromptSubmit adopts the id claude settled on. No-op for the
	/// shell pane, when <c>claude.resumeSession</c> is off, or for any other event.
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
	/// Resolves this directory's managed Claude launch — the stable session id and whether to <c>--resume</c> or
	/// create it with <c>--session-id</c>. Null when resume is unconfigured or off (claude then picks its own id).
	/// The resume policy lives in <see cref="ClaudeSessionStore"/>.
	/// </summary>
	private ClaudeLaunch? ResolveClaudeLaunch() {
		if (ClaudeSessions is not { } store || !_settings.GetBool("claude.resumeSession", fallback: true)) {
			return null;
		}

		var launch = store.Resolve(Workspace);
		// A resume only works if Claude still holds the transcript for this id under this cwd. When it doesn't
		// (the conversation was cleared, or filed under a different directory), create it fresh under the same id
		// rather than launch a doomed --resume that greets the user with "No conversation found".
		if (launch.Resume && ClaudeTranscripts is { } transcripts && !transcripts.Exists(Workspace, launch.SessionId)) {
			return launch with { Resume = false };
		}

		return launch;
	}

	/// <summary>Tears down the current PTY child and its optional log; the supervisor calls this on stop/dispose.</summary>
	private void StopTerminal() {
		// Detach under the gate, dispose outside it: disposing a ConPTY blocks until the child exits, and that
		// child's final output can fire OnOutput → ObserveClaudeStartup, which itself takes _gate — holding the
		// gate across Dispose would deadlock the two.
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

	private void OnOutput(byte[] data, TerminalModeTracker modes) {
		_ptyLog?.Write(data, 0, data.Length);
		_ptyLog?.Flush();
		// Persist to the scrollback log for replay on a cold (re)attach / resume.
		_scrollback?.Append(data);
		// Latch mode/title changes for the restore preamble a reattaching client gets (see OnReady).
		modes.Feed(data);

		// Confirm a managed claude launch came up (and self-heal on a failed resume), independent of the page.
		ObserveClaudeStartup(data);

		// Output always posts, tagged by slot: a background session paints into its own hidden pane (instant
		// switch). The page drops a background backend's traffic at the bridge, so this never bleeds across backends.
		_bridge.PostToWeb(TermOutputJson(data));
	}

	/// <summary>The <c>term-output</c> bridge message carrying <paramref name="data"/> base64-encoded for this pane.</summary>
	private string TermOutputJson(ReadOnlySpan<byte> data) =>
		$"{{\"slot\":\"{_slotEncoded}\",\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{Convert.ToBase64String(data)}\"}}";

	/// <summary>
	/// Feeds claude's early output to the per-launch <see cref="ClaudeStartupWatcher"/> so it can later tell a
	/// launch that came up from one that died at startup (consumed by <see cref="OnTerminalExited"/>). Does NOT
	/// mark the session resumable — that's <see cref="ObserveHook"/>, on a real user message.
	/// </summary>
	private void ObserveClaudeStartup(byte[] data) {
		lock (_gate) {
			// Only decode while a managed launch is still unconfirmed; once up, output is irrelevant here.
			if (_startupWatcher is { Confirmed: false } watcher) {
				watcher.Observe(Encoding.UTF8.GetString(data));
			}
		}
	}

	/// <summary>
	/// The PTY child exited. A managed claude launch that died before confirming startup (a dead/poison session
	/// id, not a healthy run the user quit) heals the session store so the relaunch recovers: a failed
	/// <c>--resume</c> re-creates the same id, a failed <c>--session-id</c> forgets it (next launch cold-starts).
	/// An intentional stop and a clean exit are left alone; the supervisor is notified last to apply its policy.
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
		// Posts to this session's own (slot-tagged) pane; the page drops it if its backend isn't active.
		Console.WriteLine($"[weavie] terminal[{_session}] child exited: {code}");
		Console.Out.Flush();
		_bridge.PostToWeb($"{{\"slot\":\"{_slotEncoded}\",\"type\":\"term-exit\",\"session\":\"{_session}\",\"code\":{code}}}");
	}

	private void PostNotice(string text) {
		string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
		_bridge.PostToWeb($"{{\"slot\":\"{_slotEncoded}\",\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{base64}\"}}");
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
