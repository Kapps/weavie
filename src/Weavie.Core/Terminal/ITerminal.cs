namespace Weavie.Core.Terminal;

/// <summary>How to launch the PTY child.</summary>
public sealed record TerminalStartInfo
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set/override on top of the current process environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Environment variable names to remove from the child (e.g. ANTHROPIC_API_KEY).</summary>
    public IReadOnlyList<string> RemoveEnvironment { get; init; } = [];

    public int Columns { get; init; } = 80;

    public int Rows { get; init; } = 24;
}

/// <summary>
/// The terminal seam (the third injected interface alongside the agent backend and the
/// filesystem). Real implementation = a PTY running a real process; test fake = scripted
/// bytes. No fallbacks.
/// </summary>
public interface ITerminal : IDisposable
{
    /// <summary>Raised with raw bytes the child wrote to the PTY (off the UI thread).</summary>
    event Action<byte[]>? Output;

    /// <summary>Raised once with the child's exit code when it terminates.</summary>
    event Action<int>? Exited;

    bool IsRunning { get; }

    void Start(TerminalStartInfo startInfo);

    /// <summary>Writes input bytes (keystrokes) to the child.</summary>
    void Write(byte[] data);

    void Resize(int columns, int rows);
}
