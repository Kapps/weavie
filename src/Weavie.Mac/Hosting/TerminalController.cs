using Weavie.Core.Configuration;
using Weavie.Core.Terminal;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Ties one real PTY to an xterm.js pane over the bridge. Each controller drives a single
/// <em>session</em>: <c>"claude"</c> launches the interactive <c>claude</c> TUI (via a login shell so
/// PATH/env resolve regardless of how the app started, exec'ing the <c>claude.path</c> setting, with
/// <c>ANTHROPIC_API_KEY</c> stripped so billing stays on the user's subscription — interactive CLI =
/// full plan, never <c>-p</c>/SDK); <c>"shell"</c> launches the shell named by the
/// <c>terminal.shell</c> setting. The session id tags every <c>term-output</c>/<c>term-exit</c> message
/// so the page routes it to the matching pane. Only the claude session optionally tees raw PTY bytes
/// to WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP handshake in step 3).
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly HostBridge _bridge;
	private readonly string _session;
	private readonly SettingsStore _settings;
	private readonly object _gate = new();
	private PosixPtyTerminal? _terminal;
	private FileStream? _ptyLog;
	private volatile bool _suppressExit;

	/// <summary>
	/// Creates a controller that streams PTY output to (and input from) the given bridge, resolving its
	/// shell/claude/workspace from <paramref name="settings"/>. <paramref name="session"/> is the pane
	/// this controller feeds: <c>"claude"</c> or <c>"shell"</c>.
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
	}

	/// <summary>Extra environment to inject into the spawned claude (used by the MCP wiring).</summary>
	public IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>The directory claude runs in (and the IDE workspace). Defaults to the <c>workspace</c> setting.</summary>
	public string Workspace { get; set; }

	/// <summary>
	/// Spawns this session's child (claude via a login shell, or the configured shell) at the given size
	/// and begins relaying its output to the web view. No-op if already started.
	/// </summary>
	public void Start(int columns, int rows) {
		lock (_gate) {
			if (_terminal is not null) {
				return;
			}

			_suppressExit = false;
			var isClaude = _session == "claude";

			// Only the claude session tees to WEAVIE_PTY_LOG: both sessions sharing one path would
			// clash on the exclusive FileStream, and the log exists for the IDE-MCP handshake anyway.
			var logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
			if (isClaude && !string.IsNullOrEmpty(logPath)) {
				_ptyLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
			}

			var (command, arguments) = isClaude ? ResolveClaudeLauncher() : ResolveShellLauncher();

			var workspace = Workspace;
			var terminal = new PosixPtyTerminal();
			terminal.Output += OnOutput;
			terminal.Exited += OnExited;
			terminal.Start(new TerminalStartInfo {
				Command = command,
				Arguments = arguments,
				WorkingDirectory = workspace,
				// Only the claude session needs the key stripped + the MCP discovery env injected.
				RemoveEnvironment = isClaude ? ["ANTHROPIC_API_KEY"] : [],
				Environment = isClaude ? ExtraEnvironment : new Dictionary<string, string>(StringComparer.Ordinal),
				Columns = columns,
				Rows = rows,
			});
			_terminal = terminal;
			Console.WriteLine($"[weavie] terminal[{_session}] started: {command} {string.Join(' ', arguments)} in {workspace} ({columns}x{rows})");
			Console.Out.Flush();
		}
	}

	/// <summary>
	/// Tears down the running child and asks the page to reset this pane (which re-emits
	/// <c>term-ready</c> → <see cref="Start"/>), so a changed shell takes effect live. No-op if not running.
	/// </summary>
	public void Restart() {
		lock (_gate) {
			if (_terminal is null) {
				return;
			}

			_suppressExit = true; // the impending EOF is our doing, not a child crash
			_terminal.Dispose();
			_terminal = null;
			_ptyLog?.Dispose();
			_ptyLog = null;
		}

		Console.WriteLine($"[weavie] terminal[{_session}] restarting (setting changed)");
		Console.Out.Flush();
		_bridge.PostToWeb($"{{\"type\":\"term-reset\",\"session\":\"{_session}\"}}");
	}

	/// <summary>
	/// Launches claude through a POSIX login shell (for full PATH/env) that execs the <c>claude.path</c>
	/// setting — <c>-l</c> for the login environment, <c>-c "exec &lt;claude&gt;"</c> to replace the shell.
	/// </summary>
	private (string Command, IReadOnlyList<string> Arguments) ResolveClaudeLauncher() {
		var claude = _settings.GetString("claude.path") ?? "claude";
		return (LoginShell(), ["-l", "-c", $"exec {claude}"]);
	}

	/// <summary>
	/// Resolves the plain-terminal shell from the <c>terminal.shell</c> setting to a launchable path,
	/// passing <c>-l -i</c> only to POSIX login shells (zsh/bash/sh) so the prompt + rc files load; other
	/// shells (nushell, fish, …) open at their prompt with no flags.
	/// </summary>
	private (string Command, IReadOnlyList<string> Arguments) ResolveShellLauncher() {
		var shell = _settings.GetString("terminal.shell") ?? LoginShell();
		var command = ExecutableFinder.FindOnPath(shell) ?? shell;
		var name = Path.GetFileNameWithoutExtension(command);
		IReadOnlyList<string> arguments = name is "zsh" or "bash" or "sh" ? ["-l", "-i"] : [];
		return (command, arguments);
	}

	/// <summary>The system login shell used to wrap claude: <c>$SHELL</c> if it exists, else <c>/bin/zsh</c>.</summary>
	private static string LoginShell() {
		var shell = Environment.GetEnvironmentVariable("SHELL");
		return !string.IsNullOrEmpty(shell) && File.Exists(shell) ? shell : "/bin/zsh";
	}

	/// <summary>Writes raw input bytes (keystrokes from xterm.js) to the PTY.</summary>
	public void Write(byte[] data) => _terminal?.Write(data);

	/// <summary>Resizes the PTY to the given column/row count.</summary>
	public void Resize(int columns, int rows) => _terminal?.Resize(columns, rows);

	private void OnOutput(byte[] data) {
		_ptyLog?.Write(data, 0, data.Length);
		_ptyLog?.Flush();
		var b64 = Convert.ToBase64String(data);
		_bridge.PostToWeb($"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{b64}\"}}");
	}

	private void OnExited(int code) {
		if (_suppressExit) {
			return; // a Restart() tear-down — the pane is being reset, not closed
		}

		_bridge.PostToWeb($"{{\"type\":\"term-exit\",\"session\":\"{_session}\",\"code\":{code}}}");
		Console.WriteLine($"[weavie] terminal[{_session}] child exited: {code}");
		Console.Out.Flush();
	}

	/// <summary>Tears down the PTY child process and closes the optional PTY debug log.</summary>
	public void Dispose() {
		lock (_gate) {
			_terminal?.Dispose();
			_terminal = null;
			_ptyLog?.Dispose();
			_ptyLog = null;
		}
	}
}
