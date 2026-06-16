using Weavie.Core.Terminal;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Ties one real PTY to an xterm.js pane over the bridge. Each controller drives a single
/// <em>session</em>: <c>"claude"</c> launches the interactive <c>claude</c> TUI (via a login shell so
/// PATH/env resolve regardless of how the app started, with <c>ANTHROPIC_API_KEY</c> stripped so
/// billing stays on the user's subscription — interactive CLI = full plan, never <c>-p</c>/SDK);
/// <c>"shell"</c> launches a plain interactive login shell. The session id tags every
/// <c>term-output</c>/<c>term-exit</c> message so the page routes it to the matching pane. Only the
/// claude session optionally tees raw PTY bytes to WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP
/// handshake in step 3).
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly HostBridge _bridge;
	private readonly string _session;
	private readonly object _gate = new();
	private PosixPtyTerminal? _terminal;
	private FileStream? _ptyLog;

	/// <summary>
	/// Creates a controller that streams PTY output to (and input from) the given bridge.
	/// <paramref name="session"/> is the pane this controller feeds: <c>"claude"</c> or <c>"shell"</c>.
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

	/// <summary>WEAVIE_WORKSPACE if set and existing, else the user's home directory.</summary>
	public static string ResolveWorkspace() {
		var workspace = Environment.GetEnvironmentVariable("WEAVIE_WORKSPACE");
		if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace)) {
			workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		}

		return workspace;
	}

	/// <summary>
	/// Spawns this session's login shell (which execs claude, or stays an interactive shell) at the
	/// given size and begins relaying its output to the web view. No-op if already started.
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

			var shell = Environment.GetEnvironmentVariable("SHELL");
			if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) {
				shell = "/bin/zsh";
			}

			// Claude: a login shell that execs the real interactive claude. Shell: a plain interactive
			// login shell (-l for full env/PATH, -i to force the prompt + rc files).
			string[] arguments = isClaude ? ["-l", "-c", "exec claude"] : ["-l", "-i"];

			var workspace = Workspace;
			var terminal = new PosixPtyTerminal();
			terminal.Output += OnOutput;
			terminal.Exited += OnExited;
			terminal.Start(new TerminalStartInfo {
				Command = shell,
				Arguments = arguments,
				WorkingDirectory = workspace,
				// Only the claude session needs the key stripped + the MCP discovery env injected.
				RemoveEnvironment = isClaude ? ["ANTHROPIC_API_KEY"] : [],
				Environment = isClaude ? ExtraEnvironment : new Dictionary<string, string>(StringComparer.Ordinal),
				Columns = columns,
				Rows = rows,
			});
			_terminal = terminal;
			Console.WriteLine($"[weavie] terminal[{_session}] started: {shell} {string.Join(' ', arguments)} in {workspace} ({columns}x{rows})");
			Console.Out.Flush();
		}
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
