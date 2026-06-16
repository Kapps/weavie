using Weavie.Core.Terminal;
using Weavie.Win.Terminal;

namespace Weavie.Win.Hosting;

/// <summary>
/// Ties one real ConPTY to an xterm.js pane over the bridge. Each controller drives a single
/// <em>session</em>: <c>"claude"</c> launches the interactive <c>claude</c> TUI (resolved from PATH
/// or the native-installer location, with <c>ANTHROPIC_API_KEY</c> stripped so billing stays on the
/// user's subscription — interactive CLI = full plan, never <c>-p</c>/SDK); <c>"shell"</c> launches a
/// plain interactive shell. The session id tags every <c>term-output</c>/<c>term-exit</c> message so
/// the page routes it to the matching pane. Only the claude session optionally tees raw PTY bytes to
/// WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP handshake). Windows sibling of the macOS TerminalController.
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly HostBridge _bridge;
	private readonly string _session;
	private readonly Lock _gate = new();
	private WindowsConPtyTerminal? _terminal;
	private FileStream? _ptyLog;

	/// <summary>
	/// Creates the controller bound to the webview <paramref name="bridge"/> for streaming PTY output
	/// to xterm.js. <paramref name="session"/> is the pane this controller feeds: <c>"claude"</c> or
	/// <c>"shell"</c>.
	/// </summary>
	public TerminalController(HostBridge bridge, string session) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentException.ThrowIfNullOrEmpty(session);
		_bridge = bridge;
		_session = session;
	}

	/// <summary>Extra environment to inject into the spawned claude (used by the MCP wiring).</summary>
	public IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; } =
		new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>The directory claude runs in (and the IDE workspace). Defaults to <see cref="ResolveWorkspace"/>.</summary>
	public string Workspace { get; set; } = ResolveWorkspace();

	/// <summary>WEAVIE_WORKSPACE if set and existing, else the user's profile directory.</summary>
	public static string ResolveWorkspace() {
		var workspace = Environment.GetEnvironmentVariable("WEAVIE_WORKSPACE");
		if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) {
			workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}

		return workspace;
	}

	/// <summary>
	/// Launches this session's child (claude or a shell) in a ConPTY sized to the given columns and
	/// rows. Idempotent: a no-op if the terminal is already running.
	/// </summary>
	public void Start(int columns, int rows) {
		lock (_gate) {
			if (_terminal is not null) {
				return;
			}

			var isClaude = _session == "claude";

			// Only the claude session tees to WEAVIE_PTY_LOG: both sessions sharing one path would
			// clash on the exclusive FileStream, and the log exists for the IDE-MCP handshake anyway.
			var logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
			if (isClaude && !string.IsNullOrEmpty(logPath)) {
				_ptyLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
			}

			var (command, arguments) = isClaude ? ResolveClaudeLauncher() : ResolveShellLauncher();

			var workspace = Workspace;
			var terminal = new WindowsConPtyTerminal();
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
	/// Resolves how to launch claude: WEAVIE_CLAUDE override, else a <c>claude</c> binary on PATH,
	/// else the native-installer location, else bare "claude" (let CreateProcess search PATH). A
	/// <c>.cmd</c>/<c>.bat</c> shim (npm install) is run through cmd.exe; a native <c>.exe</c> is launched directly.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveClaudeLauncher() {
		var claude = Environment.GetEnvironmentVariable("WEAVIE_CLAUDE");
		if (string.IsNullOrEmpty(claude)) {
			claude = FindOnPath("claude.exe") ?? FindOnPath("claude.cmd") ?? FindOnPath("claude.bat");
		}

		if (string.IsNullOrEmpty(claude)) {
			var local = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
			claude = File.Exists(local) ? local : "claude";
		}

		var ext = Path.GetExtension(claude).ToLowerInvariant();
		if (ext is ".cmd" or ".bat") {
			var comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
			return (comspec, ["/c", claude]);
		}

		return (claude, []);
	}

	/// <summary>
	/// Resolves the plain-terminal shell: WEAVIE_SHELL override, else PowerShell 7 (<c>pwsh.exe</c>)
	/// if installed, else Windows PowerShell (<c>powershell.exe</c>, always present). <c>-NoLogo</c>
	/// suppresses the startup banner so the pane opens straight at a prompt.
	/// </summary>
	private static (string Command, IReadOnlyList<string> Arguments) ResolveShellLauncher() {
		var shell = Environment.GetEnvironmentVariable("WEAVIE_SHELL");
		if (string.IsNullOrEmpty(shell)) {
			shell = FindOnPath("pwsh.exe") ?? FindOnPath("powershell.exe") ?? "powershell.exe";
		}

		return (shell, ["-NoLogo"]);
	}

	/// <summary>Searches %PATH% for an executable file by name (e.g. <c>claude.exe</c>).</summary>
	private static string? FindOnPath(string fileName) {
		var path = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(path)) {
			return null;
		}

		foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			string candidate;
			try {
				candidate = Path.Combine(dir, fileName);
			} catch (ArgumentException) {
				continue; // a malformed PATH entry; skip it
			}

			if (File.Exists(candidate)) {
				return candidate;
			}
		}

		return null;
	}

	/// <summary>Forwards input bytes (keystrokes) to the PTY child.</summary>
	public void Write(byte[] data) => _terminal?.Write(data);

	/// <summary>Resizes the pseudo console to the given columns and rows.</summary>
	public void Resize(int columns, int rows) => _terminal?.Resize(columns, rows);

	private void OnOutput(byte[] data) {
		_ptyLog?.Write(data, 0, data.Length);
		_ptyLog?.Flush();
		var b64 = Convert.ToBase64String(data);
		_bridge.PostToWeb($"{{\"type\":\"term-output\",\"session\":\"{_session}\",\"dataB64\":\"{b64}\"}}");
	}

	private void OnExited(int code) {
		_bridge.PostToWeb($"{{\"type\":\"term-exit\",\"session\":\"{_session}\",\"code\":{code}}}");
		Console.WriteLine($"[weavie] terminal[{_session}] child exited: {code}");
		Console.Out.Flush();
	}

	/// <summary>Tears down the PTY child and the optional PTY log.</summary>
	public void Dispose() {
		lock (_gate) {
			_terminal?.Dispose();
			_terminal = null;
			_ptyLog?.Dispose();
			_ptyLog = null;
		}
	}
}
