using System.Text;

namespace Weavie.Core.Terminal;

/// <summary>
/// Scripted terminal test fake. Records writes/resizes for assertions and lets tests drive
/// output and exit deterministically — no real process, no PTY.
/// </summary>
public sealed class FakeTerminal : ITerminal {
	private readonly List<byte[]> _writes = [];

	/// <inheritdoc/>
	public event Action<byte[]>? Output;
	/// <inheritdoc/>
	public event Action<int>? Exited;

	/// <inheritdoc/>
	public bool IsRunning { get; private set; }

	/// <summary>The most recent <see cref="TerminalStartInfo"/> passed to <see cref="Start"/>, for assertions.</summary>
	public TerminalStartInfo? LastStartInfo { get; private set; }

	/// <summary>The columns/rows of the most recent <see cref="Resize"/> call.</summary>
	public (int Columns, int Rows) LastResize { get; private set; }

	/// <summary>Every byte array passed to <see cref="Write"/>, in order.</summary>
	public IReadOnlyList<byte[]> Writes => _writes;

	/// <summary>All written bytes decoded as UTF-8 and concatenated, for convenient assertions.</summary>
	public string WrittenText => string.Concat(_writes.Select(w => Encoding.UTF8.GetString(w)));

	/// <inheritdoc/>
	public void Start(TerminalStartInfo startInfo) {
		ArgumentNullException.ThrowIfNull(startInfo);
		LastStartInfo = startInfo;
		IsRunning = true;
	}

	/// <inheritdoc/>
	public void Write(byte[] data) {
		ArgumentNullException.ThrowIfNull(data);
		_writes.Add(data);
	}

	/// <inheritdoc/>
	public void Resize(int columns, int rows) => LastResize = (columns, rows);

	/// <summary>Test driver: emit output bytes as if the child wrote them.</summary>
	public void EmitOutput(byte[] data) => Output?.Invoke(data);

	/// <summary>Test driver: emit <paramref name="text"/> (UTF-8 encoded) as if the child wrote it.</summary>
	public void EmitOutput(string text) => Output?.Invoke(Encoding.UTF8.GetBytes(text));

	/// <summary>Test driver: simulate the child exiting.</summary>
	public void EmitExit(int code) {
		IsRunning = false;
		Exited?.Invoke(code);
	}

	/// <inheritdoc/>
	public void Dispose() => IsRunning = false;
}
