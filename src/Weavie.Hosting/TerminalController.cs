using System.Text;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Processes;
using Weavie.Core.Terminal;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

/// <summary>
/// Ties one provider-neutral child lifecycle to a real PTY and xterm.js pane over its session channel. The launch source
/// owns executable, environment, cwd, capture, and restart-time state; the OS-specific half is an injected
/// <see cref="IPtyLauncher"/>, so the controller is identical for agents and shells on every host. The child runs under a
/// <see cref="ProcessSupervisor"/> with a lifecycle-specific restart policy: permanent agent panes relaunch,
/// while closeable shell/authentication tabs remain exited until the user reopens them.
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly MessageFeatureChannel _messages;
	private readonly string _pane;
	private readonly SettingsStore _settings;
	private readonly IPtyLauncher _launcher;
	private readonly ITerminalProcess _process;
	private readonly Lock _gate = new();
	private readonly ProcessSupervisor _supervisor;
	private ITerminal? _terminal;
	private FileStream? _ptyLog;
	private int _columns = 80;
	private int _rows = 24;
	// Shell-only: the on-disk scrollback log this pane's output is tee'd to, for replay on (re)attach and faded
	// history on resume. Unlike _ptyLog it survives process restarts and is closed only on dispose.
	private ScrollbackLog? _scrollback;
	// The last valid directory reported via OSC 7, used only when the launch source opts into following it.
	private string? _reportedCwd;
	private AgentWorkingDirectoryMode _workingDirectoryMode = AgentWorkingDirectoryMode.Fixed;
	// Per-launch latched terminal state (alt screen, mouse modes, bracketed paste, title…), replayed to a client
	// that mounts onto the already-live child — the resize nudge redraws content but can't re-establish modes.
	// Replaced with a fresh instance per launch in StartTerminal, which is also the reset on restart.
	private TerminalModeTracker _modes = new();
	// Serializes live output posts against a pending reset→replay (_resyncPending, set while a ResyncPane
	// awaits its ready event), so every output chunk reaches the page exactly once: via the replay when it was
	// logged before the replay snapshot, live otherwise. Separate from _gate so keystrokes never wait on it.
	private readonly Lock _replayGate = new();
	private bool _resyncPending;
	// Batches live PTY output into fewer, larger terminal output events so a burst can't flood the transport's bounded
	// outbox and freeze the page. Scrollback is logged unbatched; only the live post is deferred.
	private readonly TerminalOutputCoalescer _coalescer;

	/// <summary>
	/// Creates a controller that streams PTY output to (and input from) <paramref name="messages"/>, resolving its
	/// pane state from <paramref name="settings"/> and spawning the child through <paramref name="launcher"/>.
	/// <paramref name="pane"/> identifies the pane in diagnostics.
	/// </summary>
	public TerminalController(
		MessageFeatureChannel messages,
		string pane,
		SettingsStore settings,
		IPtyLauncher launcher,
		ITerminalProcess process)
		: this(messages, pane, settings, launcher, process, RestartPolicy.Always) {
	}

	internal TerminalController(
		MessageFeatureChannel messages,
		string pane,
		SettingsStore settings,
		IPtyLauncher launcher,
		ITerminalProcess process,
		RestartPolicy restartPolicy) {
		ArgumentNullException.ThrowIfNull(messages);
		ArgumentException.ThrowIfNullOrEmpty(pane);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(launcher);
		ArgumentNullException.ThrowIfNull(process);
		_messages = messages;
		_pane = pane;
		_settings = settings;
		_launcher = launcher;
		_process = process;
		_coalescer = new TerminalOutputCoalescer(
			bytes => PublishOutput(bytes, replay: false),
			settings.RequireInt(CoreSettings.TerminalOutputCoalesceMs));
		Workspace = settings.GetString(CoreSettings.Workspace)
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_supervisor = new ProcessSupervisor(
			$"terminal:{pane}",
			StartTerminal,
			StopTerminal,
			new SupervisionOptions { Policy = restartPolicy },
			LogSupervisor,
			clock: null);
		_supervisor.StateChanged += OnSupervisorStateChanged;
	}

	/// <summary>The directory this terminal is rooted in. Defaults to the <c>workspace</c> setting.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// When set, the file this pane's output is persisted to so a reattaching/resumed client
	/// replays a coherent screen (faded). Setting it opens the log, capped by <c>terminal.persistScrollbackKb</c>
	/// (0 disables). A null path leaves replay to the child process.
	/// </summary>
	public string? ScrollbackLogPath {
		get;
		set {
			field = value;
			EnsureScrollbackLog();
		}
	}

	/// <summary>
	/// Raised on every supervisor transition for this session's process, so a per-session status indicator can map
	/// it (crash/crash-loop → Error, post-crash restart → Starting). Delivered in transition order on the
	/// supervisor's observer lane.
	/// </summary>
	public event Action<SupervisorStateChanged>? SupervisorChanged;

	/// <summary>Whether this controller currently owns a live PTY child.</summary>
	public bool IsRunning {
		get {
			lock (_gate) return _terminal is not null;
		}
	}

	/// <summary>
	/// Handles the page's terminal <c>ready</c> event for this pane (a session's xterm mounting). If the child isn't running
	/// it launches it sized to the given columns/rows. If it's already live (a cold reattach), the fresh xterm has
	/// missed everything the child established at startup, so this replays the persisted scrollback (shell only),
	/// then the latched terminal modes (alt screen, mouse tracking, bracketed paste, title — without which a
	/// fullscreen TUI renders into the normal buffer and grows scrollback it never wanted), and only then nudges
	/// the PTY size (one row shorter, then back) to make the TUI redraw — into the now-correct buffer. The redraw
	/// bytes can't overtake the preamble: they only exist after the resize reaches the child and come back through
	/// the PTY read thread. The start runs after the lock (the supervisor's start callback takes the gate).
	/// </summary>
	public void OnReady(int columns, int rows) =>
		OnReadyCore(
			columns,
			rows,
			(data, replay) => PublishOutput(data, replay));

	internal void OnReady(
		MessageTargetFeature messages,
		int columns,
		int rows) =>
		OnReadyCore(
			columns,
			rows,
			(data, replay) => messages.Publish(
				"output",
				new { dataB64 = Convert.ToBase64String(data), replay }));

	private void OnReadyCore(
		int columns,
		int rows,
		Action<byte[], bool> publishOutput) {
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

		// Replay persisted scrollback before (re)starting, so faded history paints above the new child's live
		// output. File I/O stays outside _gate. BuildReplay is empty when persistence is not configured.
		// Under _replayGate with the pending-resync clear: output logged before this snapshot arrives via the
		// replay, output logged after posts live below it — once each, in order (see OnOutput).
		lock (_replayGate) {
			// A scrollback pane's buffered live bytes are already in the log the replay below rebuilds, so drop
			// them to avoid double-painting; a pane without a log has no replay, so flush them ahead of the
			// mode-restore preamble instead.
			if (_scrollback is null) {
				_coalescer.Flush();
			} else {
				_coalescer.Discard();
			}

			byte[] scrollback = _scrollback?.BuildReplay() ?? [];
			if (scrollback.Length > 0) {
				publishOutput(scrollback, true);
			}

			_resyncPending = false;
		}

		if (start) {
			_supervisor.Start();
			return;
		}

		if (restore.Length > 0) {
			publishOutput(restore, true);
		}

		lock (_gate) {
			if (_terminal is not null) {
				NudgeResize(_terminal);
			}
		}
	}

	/// <summary>
	/// Re-syncs this pane's already-mounted xterm after a bridge reconnect: output posted while the link was down
	/// never reached the page. A scrollback-backed pane is reset, and its terminal <c>ready</c> reply
	/// replays the log — gap included — via <see cref="OnReady(int,int)"/>; a pane with no log keeps its buffer
	/// and gets the size nudge so the running TUI repaints its screen. No-op until the child has started.
	/// </summary>
	public void ResyncPane() =>
		ResyncPaneCore(respawn => PostTermReset(respawn));

	internal void ClearScrollback() => _scrollback?.Clear();

	internal void ResyncPane(MessageTargetFeature messages) =>
		ResyncPaneCore(respawn => messages.Publish("reset", new { respawn }));

	private void ResyncPaneCore(Action<bool> reset) {
		lock (_gate) {
			if (_terminal is null) {
				return;
			}

			if (_scrollback is null) {
				NudgeResize(_terminal);
				return;
			}
		}

		// Suppress live output until the page's ready event replays the log (OnReady clears it): a chunk posted
		// after the page clears but logged before the replay snapshot would otherwise paint twice.
		lock (_replayGate) {
			if (_resyncPending) {
				return; // a reset is already in flight; its ready reply covers this resync too
			}

			_resyncPending = true;
			// Buffered live bytes are in the log the coming replay rebuilds; drop them so they don't paint twice.
			_coalescer.Discard();
		}

		reset(false);
	}

	// One row shorter then back: the size change is what forces a running TUI to repaint the whole screen.
	// Callers hold _gate.
	private void NudgeResize(ITerminal terminal) {
		terminal.Resize(_columns, Math.Max(1, _rows - 1));
		terminal.Resize(_columns, _rows);
	}

	/// <summary>
	/// Starts the child at the cached size without the page binding to this pane — brings a session's backend up
	/// in the background ("load, don't open"); <see cref="OnReady(int,int)"/> later nudges it to the real pane size. No-op
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
	/// Intentionally tears down the running child (no auto-restart) and asks the page to reset this pane;
	/// its next terminal <c>ready</c> event starts a fresh child.
	/// </summary>
	public void Restart() {
		_supervisor.Stop();
		Console.WriteLine($"[weavie] terminal[{_pane}] restarting");
		Console.Out.Flush();
		// respawn=true: the child relaunches and re-establishes its modes, so the page does a full reset.
		PostTermReset(respawn: true);
	}

	internal void Stop() => _supervisor.Stop();

	/// <summary>
	/// The terminal <c>reset</c> event: the page clears this pane and re-emits <c>ready</c>. Respawn
	/// also resets terminal modes (the child relaunched); a still-running child keeps them.
	/// </summary>
	private void PostTermReset(bool respawn) =>
		_messages.Publish("reset", new { respawn });

	/// <summary>Opens the scrollback log for the configured path once, honoring the size-cap setting (0 = disabled).</summary>
	private void EnsureScrollbackLog() {
		if (ScrollbackLogPath is null || _scrollback is not null) {
			return;
		}

		long kb = _settings.RequireInt(CoreSettings.TerminalPersistScrollbackKb);
		if (kb > 0) {
			int capBytes = (int)Math.Min(kb, int.MaxValue / 1024) * 1024;
			_scrollback = new ScrollbackLog(ScrollbackLogPath, capBytes);
		}
	}

	/// <summary>
	/// Records the working directory the child reported via OSC 7 when its launch opts into following it. OSC 7 is
	/// untrusted terminal output, so the path is confined to this session's worktree — it can't point the
	/// relaunched process at an arbitrary directory (a binary/DLL-planting or info-disclosure vector). Fixed launches,
	/// paths outside the workspace, and paths that no longer exist are ignored.
	/// </summary>
	public void OnCwdReported(string cwd) {
		if (_workingDirectoryMode == AgentWorkingDirectoryMode.Fixed
			|| !BufferStore.IsWithinWorkspace(Workspace, cwd)
			|| !Directory.Exists(cwd)) {
			return;
		}

		lock (_gate) {
			_reportedCwd = cwd;
		}
	}

	/// <summary>Spawns a fresh PTY child at the cached size; the supervisor calls this on first start and each restart.</summary>
	private void StartTerminal(SupervisedLaunch launch) {
		lock (_gate) {
			_terminal?.Dispose();
			_ptyLog?.Dispose();

			var logical = _process.ResolveLaunch();
			_workingDirectoryMode = logical.WorkingDirectoryMode;
			string workspace = logical.WorkingDirectoryMode == AgentWorkingDirectoryMode.FollowReported
				? _reportedCwd ?? logical.WorkingDirectory
				: logical.WorkingDirectory;
			_ptyLog = logical.OutputCapture is AgentOutputCapture.File capture
				? new FileStream(capture.Path, FileMode.Create, FileAccess.Write, FileShare.Read)
				: null;
			var modes = new TerminalModeTracker();
			_modes = modes;
			var resolved = _launcher.Resolve(logical);

			var terminal = _launcher.CreateTerminal();
			// The tracker is captured per launch (not read from the field) so a late chunk from a dying child can
			// never latch into the next launch's restore preamble.
			terminal.Output += data => OnOutput(data, modes);
			terminal.Exited += code => OnTerminalExited(code, launch);
			terminal.Start(new TerminalStartInfo {
				Command = resolved.Command,
				Arguments = resolved.Arguments,
				WorkingDirectory = workspace,
				RemoveEnvironment = resolved.RemoveEnvironment,
				Environment = resolved.Environment,
				Columns = _columns,
				Rows = _rows,
			});
			_terminal = terminal;
			// Everything logged before now belongs to the previous process: mark the boundary so a replay
			// renders it faded and this new process's output live below it.
			_scrollback?.MarkBoundary();
			Console.WriteLine($"[weavie] terminal[{_pane}] started (attempt {launch.Attempt}): {resolved.Command} {string.Join(' ', resolved.Arguments)} in {workspace} ({_columns}x{_rows})");
			Console.Out.Flush();
		}

		if (launch.Attempt > 0) {
			PostNotice($"\r\n[weavie] {_pane} exited - restarting...\r\n");
		}
	}

	/// <summary>Tears down the current PTY child and its optional log; the supervisor calls this on stop/dispose.</summary>
	private void StopTerminal() {
		// Detach under the gate, dispose outside it: disposing a ConPTY blocks until the child exits, whose final
		// output can re-enter this controller. Holding the gate across Dispose would deadlock the two.
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
				StopTerminal();
				PostExit(exitedCode);
				break;
			case SupervisorState.Failed when change.ExitCode is int failedCode:
				PostNotice($"\r\n[weavie] {_pane} crashed repeatedly - stopped.\r\n");
				PostExit(failedCode);
				break;
			default:
				break;
		}
	}

	/// <summary>
	/// Raised (on the caller's thread) with each chunk written to the PTY via <see cref="Write"/> — the input
	/// side of the pane. Consumers may use it for interactions that structured provider events do not report.
	/// </summary>
	public event Action<byte[]>? InputWritten;

	/// <summary>Writes raw input bytes (keystrokes from xterm.js) to the PTY.</summary>
	public void Write(byte[] data) => _ = TryWrite(data);

	/// <summary>Writes raw input bytes when a PTY child is live, returning whether the bytes were accepted.</summary>
	public bool TryWrite(byte[] data) {
		lock (_gate) {
			if (_terminal is null) {
				return false;
			}
			_terminal.Write(data);
		}

		_process.ObserveTerminalInput(data);
		InputWritten?.Invoke(data);
		return true;
	}

	/// <summary>
	/// Writes <paramref name="text"/> to the PTY wrapped in bracketed-paste markers (<c>ESC[200~</c>…<c>ESC[201~</c>)
	/// — the framing that makes a full-screen TUI treat it as pasted input rather than typed text.
	/// </summary>
	public void WriteBracketedPaste(string text) {
		ArgumentNullException.ThrowIfNull(text);
		Write(Encoding.UTF8.GetBytes(Bracketed(text)));
	}

	/// <summary>
	/// Submits one agent turn: every part in <paramref name="parts"/> bracketed-paste framed, then the submit key,
	/// in a single write. A TUI classifies a burst of raw input as a paste, so a submit key sharing that burst is
	/// absorbed as pasted text and the turn is never sent — the framing is what tells the two apart, and one write
	/// keeps the boundary from depending on how the child's reads happen to split.
	/// </summary>
	public void WriteAgentTurn(IReadOnlyList<string> parts) {
		ArgumentNullException.ThrowIfNull(parts);
		Write(Encoding.UTF8.GetBytes(string.Concat(parts.Select(Bracketed)) + "\r"));
	}

	private static string Bracketed(string text) => $"\x1b[200~{text}\x1b[201~";

	/// <summary>
	/// Whether a job (build, dev server) is running in this pane — the PTY's foreground process group
	/// differs from the child shell. False when nothing runs. Feeds the update drain gate.
	/// </summary>
	public bool HasForegroundJob {
		get {
			lock (_gate) {
				return _terminal is { HasForegroundJob: true };
			}
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
		// Latch mode/title changes for the restore preamble a reattaching client gets (see OnReady). Independent
		// of the resync suppression below: a suppressed chunk still changes the child's mode state.
		modes.Feed(data);

		_process.ObserveTerminalOutput(data);

		// Output always posts on this session's feature: a background session paints into its own retained pane.
		// Log + post atomically under _replayGate: during a pending resync the chunk is only logged — the page
		// just cleared this pane, and the coming replay (snapshotted after this append) already delivers it.
		lock (_replayGate) {
			_scrollback?.Append(data);
			if (!_resyncPending) {
				_coalescer.Add(data);
			}
		}
	}

	/// <summary>
	/// Publishes <paramref name="data"/> base64-encoded on this pane's session-owned feature.
	/// <paramref name="replay"/> marks reattach-synthesized bytes (scrollback replay, mode restore): the page must
	/// not let its xterm answer device queries inside them — the replies would reach the child as garbage input.
	/// </summary>
	private void PublishOutput(ReadOnlySpan<byte> data, bool replay) =>
		_messages.Publish("output", new { dataB64 = Convert.ToBase64String(data), replay });

	/// <summary>
	/// Reports the PTY exit to the launch source before notifying the supervisor, preserving provider recovery
	/// decisions before the next supervised start.
	/// </summary>
	private void OnTerminalExited(int code, SupervisedLaunch launch) {
		try {
			// Only the current instance's exit informs the launch source — a stopped predecessor's late exit
			// must not consume the replacement's startup watcher or fail a healthy session's resume state.
			if (launch.IsCurrent) {
				// A deliberate stop clears the current launch before killing the child, so reaching here while
				// Running means the exit was unexpected — a startup failure to heal.
				_process.ObserveProcessExit(new AgentProcessExit {
					ExitCode = code,
					Unexpected = _supervisor.State == SupervisorState.Running,
				});
			}
		} finally {
			launch.NotifyExited(code);
		}
	}

	private void PostExit(int code) {
		// Posts to this session's pane; its client state receives the exit whether or not the pane is visible.
		Console.WriteLine($"[weavie] terminal[{_pane}] child exited: {code}");
		Console.Out.Flush();
		_coalescer.Flush(); // the child's final output must reach the page before the exit marker
		_messages.Publish("exit", new { code });
	}

	private void PostNotice(string text) {
		_coalescer.Flush(); // a notice (e.g. "restarting…") follows prior output, so drain it first
		PublishOutput(Encoding.UTF8.GetBytes(text), replay: false);
	}

	private static void LogSupervisor(SupervisorLogEntry entry) {
		Console.WriteLine($"[weavie] supervisor[{entry.Name}] {entry.Level}: {entry.Message}");
		Console.Out.Flush();
	}

	/// <summary>Tears down the supervised PTY child process and closes the optional PTY debug log + scrollback log.</summary>
	public void Dispose() {
		_supervisor.Dispose();
		_coalescer.Dispose();
		lock (_gate) {
			_ptyLog?.Dispose();
			_ptyLog = null;
			// Close the handle but leave the on-disk content, so a later resume of this worktree can replay it faded.
			_scrollback?.Dispose();
			_scrollback = null;
		}
	}
}
