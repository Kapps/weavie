using Weavie.Core.Configuration;
using Weavie.Core.Terminal;
using Weavie.Win.Terminal;

namespace Weavie.Win.Hosting;

/// <summary>
/// Ties one real ConPTY to an xterm.js pane over the bridge. Each controller drives a single
/// <em>session</em>: <c>"claude"</c> launches the interactive <c>claude</c> TUI (resolved from the
/// <c>claude.path</c> setting, with <c>ANTHROPIC_API_KEY</c> stripped so billing stays on the user's
/// subscription — interactive CLI = full plan, never <c>-p</c>/SDK); <c>"shell"</c> launches the shell
/// named by the <c>terminal.shell</c> setting. The session id tags every
/// <c>term-output</c>/<c>term-exit</c> message so the page routes it to the matching pane. Only the
/// claude session optionally tees raw PTY bytes to WEAVIE_PTY_LOG for debugging (e.g. the IDE-MCP
/// handshake). Windows sibling of the macOS TerminalController.
/// </summary>
public sealed class TerminalController : IDisposable {
	private readonly HostBridge _bridge;
	private readonly string _session;
	private readonly SettingsStore _settings;
	private readonly Lock _gate = new();
	private WindowsConPtyTerminal? _terminal;
	private FileStream? _ptyLog;
	private volatile bool _suppressExit;

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
	/// Launches this session's child (claude or a shell) in a ConPTY sized to the given columns and
	/// rows. Idempotent: a no-op if the terminal is already running.
	/// </summary>
	public void Start(int columns, int rows) {
		lock (_gate) {
			if (_terminal is not null) {
				return;
			}

			_suppressExit = false;
			bool isClaude = _session == "claude";

			// Only the claude session tees to WEAVIE_PTY_LOG: both sessions sharing one path would
			// clash on the exclusive FileStream, and the log exists for the IDE-MCP handshake anyway.
			string? logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
			if (isClaude && !string.IsNullOrEmpty(logPath)) {
				_ptyLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
			}

			var (command, arguments) = isClaude ? ResolveClaudeLauncher() : ResolveShellLauncher();

			string workspace = Workspace;
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

	/// <summary>Forwards input bytes (keystrokes) to the PTY child.</summary>
	public void Write(byte[] data) => _terminal?.Write(data);

	/// <summary>Resizes the pseudo console to the given columns and rows.</summary>
	public void Resize(int columns, int rows) => _terminal?.Resize(columns, rows);

	private void OnOutput(byte[] data) {
		_ptyLog?.Write(data, 0, data.Length);
		_ptyLog?.Flush();
		string b64 = Convert.ToBase64String(data);
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
