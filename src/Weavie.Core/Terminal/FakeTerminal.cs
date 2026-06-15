using System.Text;

namespace Weavie.Core.Terminal;

/// <summary>
/// Scripted terminal test fake. Records writes/resizes for assertions and lets tests drive
/// output and exit deterministically — no real process, no PTY.
/// </summary>
public sealed class FakeTerminal : ITerminal
{
    private readonly List<byte[]> _writes = [];

    public event Action<byte[]>? Output;
    public event Action<int>? Exited;

    public bool IsRunning { get; private set; }

    public TerminalStartInfo? LastStartInfo { get; private set; }

    public (int Columns, int Rows) LastResize { get; private set; }

    public IReadOnlyList<byte[]> Writes => _writes;

    public string WrittenText => string.Concat(_writes.Select(w => Encoding.UTF8.GetString(w)));

    public void Start(TerminalStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        LastStartInfo = startInfo;
        IsRunning = true;
    }

    public void Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _writes.Add(data);
    }

    public void Resize(int columns, int rows) => LastResize = (columns, rows);

    /// <summary>Test driver: emit output bytes as if the child wrote them.</summary>
    public void EmitOutput(byte[] data) => Output?.Invoke(data);

    public void EmitOutput(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));

    /// <summary>Test driver: simulate the child exiting.</summary>
    public void EmitExit(int code)
    {
        IsRunning = false;
        Exited?.Invoke(code);
    }

    public void Dispose() => IsRunning = false;
}
