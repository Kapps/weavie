using System.Text;

namespace Weavie.Core.Diagnostics;

/// <summary>
/// A <see cref="TextWriter"/> that forwards every write to <c>inner</c> unchanged and mirrors completed lines into
/// a <see cref="LogBuffer"/> — so the real console keeps showing output while Weavie retains an in-app copy.
/// Installed over Console.Out/Error by <see cref="LogBuffer.InstallConsoleCapture"/>.
/// </summary>
internal sealed class ConsoleTee : TextWriter {
	private readonly TextWriter _inner;
	private readonly LogBuffer _buffer;
	// Accumulates the current partial line until its terminating '\n' arrives.
	private readonly StringBuilder _pending = new();

	public ConsoleTee(TextWriter inner, LogBuffer buffer) {
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(buffer);
		_inner = inner;
		_buffer = buffer;
	}

	/// <inheritdoc/>
	public override Encoding Encoding => _inner.Encoding;

	/// <inheritdoc/>
	public override void Write(char value) {
		_inner.Write(value);
		Push(value);
	}

	/// <inheritdoc/>
	public override void Write(string? value) {
		_inner.Write(value);
		if (value is not null) {
			foreach (char c in value) {
				Push(c);
			}
		}
	}

	/// <inheritdoc/>
	public override void Flush() => _inner.Flush();

	// Feeds one character into the line accumulator: '\n' flushes the line to the buffer, '\r' is dropped (so a
	// CRLF line lands clean), anything else extends the pending line.
	private void Push(char value) {
		if (value == '\n') {
			_buffer.Append(_pending.ToString());
			_pending.Clear();
		} else if (value != '\r') {
			_pending.Append(value);
		}
	}
}
