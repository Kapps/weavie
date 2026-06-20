using System.Text;
using Weavie.Core.Configuration;
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
		}

		if (start) {
			_supervisor.Start();
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
		_bridge.PostToWeb($"{{\"type\":\"term-reset\",\"session\":\"{_session}\"}}");
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

			var launch = _launcher.Resolve(new PtyLaunchRequest {
				IsClaude = isClaude,
				Settings = _settings,
				McpConfigPath = McpConfigPath,
				SettingsFilePath = SettingsFilePath,
				SystemPromptFilePath = SystemPromptFilePath,
				ExtraEnvironment = ExtraEnvironment,
				ClaudeSessionArguments = isClaude ? ResolveClaudeSessionArgs() : [],
			});

			var terminal = _launcher.CreateTerminal();
			terminal.Output += OnOutput;
			terminal.Exited += _supervisor.NotifyExited;
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
			Console.WriteLine($"[weavie] terminal[{_session}] started (attempt {attempt}): {launch.Command} {string.Join(' ', launch.Arguments)} in {workspace} ({_columns}x{_rows})");
			Console.Out.Flush();
		}

		if (attempt > 0) {
			PostNotice($"\r\n[weavie] {_session} exited - restarting...\r\n");
		}
	}

	/// <summary>
	/// The claude session-resume flag pair for this launch — <c>["--resume", &lt;id&gt;]</c> to reattach to this
	/// directory's previous Claude conversation, or <c>["--session-id", &lt;id&gt;]</c> to create it the first
	/// time. Empty when resume is unconfigured (<see cref="ClaudeSessions"/> null) or turned off via
	/// <c>claude.resumeSession</c> (claude then picks its own id). The resume <em>policy</em> lives here, shared
	/// across hosts; each <see cref="IPtyLauncher"/> just formats these args for its OS.
	/// </summary>
	private IReadOnlyList<string> ResolveClaudeSessionArgs() {
		if (ClaudeSessions is not { } store || !_settings.GetBool("claude.resumeSession", fallback: true)) {
			return [];
		}

		var launch = store.Resolve(Workspace);
		return [launch.Resume ? "--resume" : "--session-id", launch.SessionId];
	}

	/// <summary>Tears down the current PTY child and its optional log; the supervisor calls this on stop/dispose.</summary>
	private void StopTerminal() {
		lock (_gate) {
			_terminal?.Dispose();
			_terminal = null;
			_ptyLog?.Dispose();
			_ptyLog = null;
		}
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
		if (!OutputActive) {
			return;
		}

		string base64 = Convert.ToBase64String(data);
		_bridge.PostToWeb($"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{base64}\"}}");
	}

	private void PostExit(int code) {
		_bridge.PostToWeb($"{{\"type\":\"term-exit\",\"session\":\"{_session}\",\"code\":{code}}}");
		Console.WriteLine($"[weavie] terminal[{_session}] child exited: {code}");
		Console.Out.Flush();
	}

	private void PostNotice(string text) {
		string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
		_bridge.PostToWeb($"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{base64}\"}}");
	}

	private static void LogSupervisor(SupervisorLogEntry entry) {
		Console.WriteLine($"[weavie] supervisor[{entry.Name}] {entry.Level}: {entry.Message}");
		Console.Out.Flush();
	}

	/// <summary>Tears down the supervised PTY child process and closes the optional PTY debug log.</summary>
	public void Dispose() {
		_supervisor.Dispose();
		lock (_gate) {
			_ptyLog?.Dispose();
			_ptyLog = null;
		}
	}
}
