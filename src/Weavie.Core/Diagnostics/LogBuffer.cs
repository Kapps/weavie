namespace Weavie.Core.Diagnostics;

/// <summary>
/// A thread-safe, bounded ring of the most recent console lines Weavie has emitted — the store behind the in-app
/// log viewer. When it overflows it evicts the oldest line and counts the eviction, so a truncation is never
/// silent: <see cref="Snapshot"/> returns the dropped count for the reader to see.
/// </summary>
public sealed class LogBuffer {
	/// <summary>The default ring size — how many recent lines the viewer retains.</summary>
	public const int DefaultCapacity = 5000;

	private readonly int _capacity;
	private readonly Queue<string> _lines;
	private readonly Lock _gate = new();
	private int _dropped;

	/// <summary>Creates a ring holding the most recent <paramref name="capacity"/> lines.</summary>
	public LogBuffer(int capacity) {
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
		_capacity = capacity;
		_lines = new Queue<string>(capacity);
	}

	/// <summary>Appends one line, evicting (and counting) the oldest when the ring is full.</summary>
	public void Append(string line) {
		ArgumentNullException.ThrowIfNull(line);
		lock (_gate) {
			_lines.Enqueue(line);
			while (_lines.Count > _capacity) {
				_lines.Dequeue();
				_dropped++;
			}
		}
	}

	/// <summary>The retained lines (oldest first) and how many earlier lines the ring has evicted since launch.</summary>
	public (IReadOnlyList<string> Lines, int Dropped) Snapshot() {
		lock (_gate) {
			return ([.. _lines], _dropped);
		}
	}

	/// <summary>
	/// Wraps <paramref name="inner"/> in a writer that mirrors every completed line into this buffer, then forwards
	/// the write on unchanged — so a console still shows its output while this buffer keeps a copy.
	/// </summary>
	public TextWriter Tee(TextWriter inner) => new ConsoleTee(inner, this);

	/// <summary>
	/// Tees <c>Console.Out</c> and <c>Console.Error</c> into a fresh buffer (forwarding to the real console
	/// unchanged) and returns it. Call once, from a host entry point — never a test, since it mutates process-global
	/// Console.
	/// </summary>
	public static LogBuffer InstallConsoleCapture() {
		var buffer = new LogBuffer(DefaultCapacity);
		Console.SetOut(buffer.Tee(Console.Out));
		Console.SetError(buffer.Tee(Console.Error));
		return buffer;
	}
}
