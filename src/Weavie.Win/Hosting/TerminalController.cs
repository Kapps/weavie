using System.Text;
using Weavie.Core.Configuration;
using Weavie.Core.Processes;
using Weavie.Core.Terminal;
using Weavie.Win.Terminal;

namespace Weavie.Win.Hosting;

/// <summary>
/// Ties one real ConPTY to an xterm.js pane over the bridge. Each controller drives a single
/// <em>session</em>: <c>"claude"</c> launches the interactive <c>claude</c> TUI (resolved from the
/// <c>claude.path</c> setting, with <c>ANTHROPIC_API_KEY</c> stripped so billing stays on the user's
/// subscription — interactive CLI = full plan, never <c>-p</c>/SDK); <c>"shell"</c> launches the shell
/// named by the <c>terminal.shell</c> setting. The child runs under a <see cref="ProcessSupervisor"/> with
/// <see cref="RestartPolicy.Always"/>: a pane is a permanent fixture, so any exit (clean or crash) relaunches
/// it — only the crash-loop breaker leaves a stopped pane. The session id tags every
/// <c>term-output</c>/<c>term-exit</c> message so the page routes it to the matching pane. Only the claude
/// session optionally tees raw PTY bytes to WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP handshake).
/// Windows sibling of the macOS TerminalController.
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly HostBridge _bridge;
	private readonly string _session;
	private readonly SettingsStore _settings;
	private readonly Lock _gate = new();
	private readonly ProcessSupervisor _supervisor;
	private WindowsConPtyTerminal? _terminal;
	private FileStream? _ptyLog;
	private int _columns = 80;
	private int _rows = 24;

	/// <summary>
	/// Creates the controller bound to the webview <paramref name="bridge"/> for streaming PTY output
	/// to xterm.js, resolving its shell/claude/workspace from <paramref name="settings"/>.
	/// <paramref name="session"/> is the pane this controller feeds: <c>"claude"</c> or <c>"shell"</c>.
	/// </summary>
	public TerminalController(HostBridge bridge, string session, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentException.ThrowIfNullOrEmpty(session);
		ArgumentNullException.ThrowIfNull(settings);
		_bridge = bridge;
		_session = session;
		_settings = settings;
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
	/// Raised on every supervisor transition for this session's process (start, crash, restart, give-up),
	/// so a per-session status indicator can map it (crash / crash-loop → Error, post-crash restart →
	/// Starting). Fires on the supervisor's thread; handlers must not block.
	/// </summary>
	public event Action<SupervisorStateChanged>? SupervisorChanged;

	/// <summary>
	/// Launches this session's child (claude or a shell) in a ConPTY sized to the given columns and
	/// rows, under the supervisor. Idempotent: a no-op if it is already running or restarting.
	/// </summary>
	public void Start(int columns, int rows) {
		lock (_gate) {
			_columns = columns;
			_rows = rows;
		}

		_supervisor.Start();
	}

	/// <summary>
	/// Intentionally tears down the running child (no auto-restart) and asks the page to reset this pane
	/// (which re-emits <c>term-ready</c> → <see cref="Start"/>), so a changed shell takes effect live.
	/// </summary>
	public void Restart() {
		_supervisor.Stop();
		Console.WriteLine($"[weavie] terminal[{_session}] restarting (setting changed)");
		Console.Out.Flush();
		_bridge.PostToWeb($"{{\"type\":\"term-reset\",\"session\":\"{_session}\"}}");
	}

	/// <summary>Spawns a fresh ConPTY child at the cached size; the supervisor calls this on first start and each restart.</summary>
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

			var (command, arguments) = isClaude ? ResolveClaudeLauncher() : ResolveShellLauncher();
			var terminal = new WindowsConPtyTerminal();
			terminal.Output += OnOutput;
			terminal.Exited += _supervisor.NotifyExited;
			terminal.Start(new TerminalStartInfo {
				Command = command,
				Arguments = arguments,
				WorkingDirectory = workspace,
				// Only the claude session needs the key stripped + the MCP discovery env injected.
				RemoveEnvironment = isClaude ? ["ANTHROPIC_API_KEY"] : [],
				Environment = isClaude ? ExtraEnvironment : new Dictionary<string, string>(StringComparer.Ordinal),
				Columns = _columns,
				Rows = _rows,
			});
			_terminal = terminal;
			Console.WriteLine($"[weavie] terminal[{_session}] started (attempt {attempt}): {command} {string.Join(' ', arguments)} in {workspace} ({_columns}x{_rows})");
			Console.Out.Flush();
		}

		if (attempt > 0) {
			PostNotice($"\r\n[weavie] {_session} exited - restarting...\r\n");
		}
	}

	/// <summary>Tears down the current ConPTY child and its optional log; the supervisor calls this on stop/dispose.</summary>
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

	/// <summary>Forwards input bytes (keystrokes) to the PTY child.</summary>
	public void Write(byte[] data) {
		lock (_gate) {
			_terminal?.Write(data);
		}
	}

	/// <summary>Resizes the pseudo console to the given columns and rows (and remembers them for restarts).</summary>
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

	/// <summary>
	/// Resolves how to launch claude from the <c>claude.path</c> setting: a <c>.cmd</c>/<c>.bat</c> shim
	/// (npm install) runs through cmd.exe; a native <c>.exe</c> (or a bare <c>claude</c> for CreateProcess
	/// to find on PATH) launches directly.
	/// </summary>
	private (string Command, IReadOnlyList<string> Arguments) ResolveClaudeLauncher() {
		string claude = _settings.GetString("claude.path") ?? "claude";
		var claudeArgs = new List<string>();
		if (!string.IsNullOrEmpty(McpConfigPath)) {
			claudeArgs.Add("--mcp-config");
			claudeArgs.Add(McpConfigPath);
		}
		if (!string.IsNullOrEmpty(SettingsFilePath)) {
			claudeArgs.Add("--settings");
			claudeArgs.Add(SettingsFilePath);
		}
		if (!string.IsNullOrEmpty(SystemPromptFilePath)) {
			claudeArgs.Add("--append-system-prompt-file");
			claudeArgs.Add(SystemPromptFilePath);
		}

		string ext = Path.GetExtension(claude).ToLowerInvariant();
		if (ext is ".cmd" or ".bat") {
			string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			return (comspec, ["/c", claude, .. claudeArgs]);
		}

		return (claude, claudeArgs);
	}

	/// <summary>
	/// Resolves the plain-terminal shell from the <c>terminal.shell</c> setting to a launchable command,
	/// passing <c>-NoLogo</c> only to PowerShell so its banner is suppressed; other shells (nushell, cmd,
	/// bash, …) open straight at their prompt with no flags.
	/// </summary>
	private (string Command, IReadOnlyList<string> Arguments) ResolveShellLauncher() {
		string shell = _settings.GetString("terminal.shell") ?? "powershell";
		string command = ExecutableFinder.FindOnPath(shell) ?? shell;
		string name = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
		IReadOnlyList<string> arguments = name is "pwsh" or "powershell" ? ["-NoLogo"] : [];
		return (command, arguments);
	}

	/// <summary>Tears down the supervised PTY child and the optional PTY log.</summary>
	public void Dispose() {
		_supervisor.Dispose();
		lock (_gate) {
			_ptyLog?.Dispose();
			_ptyLog = null;
		}
	}
}
