using Weavie.Core.Terminal;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Ties a real PTY (running the interactive <c>claude</c> TUI) to the xterm.js pane over the
/// bridge. Launches via a login shell so PATH/env resolve regardless of how the app started,
/// and strips <c>ANTHROPIC_API_KEY</c> so billing stays on the user's subscription (interactive
/// CLI = full plan; never <c>-p</c>/SDK). Optionally tees raw PTY bytes to WEAVIE_PTY_LOG for
/// debugging (e.g. the IDE-MCP handshake in step 3).
/// </summary>
public sealed class TerminalController : IDisposable
{
    private readonly HostBridge _bridge;
    private readonly object _gate = new();
    private PosixPtyTerminal? _terminal;
    private FileStream? _ptyLog;

    public TerminalController(HostBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
    }

    /// <summary>Extra environment to inject into the spawned claude (used by step 3's MCP wiring).</summary>
    public IReadOnlyDictionary<string, string> ExtraEnvironment { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public void Start(int columns, int rows)
    {
        lock (_gate)
        {
            if (_terminal is not null)
            {
                return;
            }

            var logPath = Environment.GetEnvironmentVariable("WEAVIE_PTY_LOG");
            if (!string.IsNullOrEmpty(logPath))
            {
                _ptyLog = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            }

            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            {
                shell = "/bin/zsh";
            }

            var workspace = Environment.GetEnvironmentVariable("WEAVIE_WORKSPACE");
            if (string.IsNullOrEmpty(workspace) || !Directory.Exists(workspace))
            {
                workspace = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            var terminal = new PosixPtyTerminal();
            terminal.Output += OnOutput;
            terminal.Exited += OnExited;
            terminal.Start(new TerminalStartInfo
            {
                Command = shell,
                // Login shell -> full user env/PATH, then exec the real interactive claude.
                Arguments = ["-l", "-c", "exec claude"],
                WorkingDirectory = workspace,
                RemoveEnvironment = ["ANTHROPIC_API_KEY"],
                Environment = ExtraEnvironment,
                Columns = columns,
                Rows = rows,
            });
            _terminal = terminal;
            Console.WriteLine($"[weavie] terminal started: {shell} -l -c 'exec claude' in {workspace} ({columns}x{rows})");
            Console.Out.Flush();
        }
    }

    public void Write(byte[] data) => _terminal?.Write(data);

    public void Resize(int columns, int rows) => _terminal?.Resize(columns, rows);

    private void OnOutput(byte[] data)
    {
        _ptyLog?.Write(data, 0, data.Length);
        _ptyLog?.Flush();
        var b64 = Convert.ToBase64String(data);
        _bridge.PostToWeb($"{{\"type\":\"term-output\",\"dataB64\":\"{b64}\"}}");
    }

    private void OnExited(int code)
    {
        _bridge.PostToWeb($"{{\"type\":\"term-exit\",\"code\":{code}}}");
        Console.WriteLine($"[weavie] terminal child exited: {code}");
        Console.Out.Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _terminal?.Dispose();
            _terminal = null;
            _ptyLog?.Dispose();
            _ptyLog = null;
        }
    }
}
