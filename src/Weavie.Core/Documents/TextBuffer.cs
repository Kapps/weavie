using System.Text;

namespace Weavie.Core.Documents;

/// <summary>
/// A minimal text buffer implementing exactly the range math the document-model
/// interface needs, with Monaco line/column semantics. Line endings are normalized
/// to "\n" on input. This is deliberately tiny — it is a <em>substitute for</em>
/// Monaco in T1 tests, not a reimplementation of it (Editor &amp; Shared Models:
/// "keep the interface tiny so parity risk is bounded").
/// </summary>
public sealed class TextBuffer {
	// Start offset of each line (0-based char offsets into _text). Rebuilt on mutation.
	private int[] _lineStarts;

	/// <summary>Creates a buffer from <paramref name="initialText"/>, normalizing line endings to "\n".</summary>
	public TextBuffer(string initialText) {
		ArgumentNullException.ThrowIfNull(initialText);
		Text = Normalize(initialText);
		_lineStarts = ComputeLineStarts(Text);
	}

	/// <summary>The current buffer contents, with "\n" line endings.</summary>
	public string Text { get; private set; }

	/// <summary>Number of lines in the buffer (always at least 1).</summary>
	public int LineCount => _lineStarts.Length;

	/// <summary>Returns the substring covered by <paramref name="range"/>.</summary>
	public string GetText(TextRange range) {
		var start = OffsetOf(range.Start);
		var end = OffsetOf(range.End);
		if (end < start) {
			throw new ArgumentException($"Range end {range.End} precedes start {range.Start}.", nameof(range));
		}

		return Text[start..end];
	}

	/// <summary>Applies a single edit. Offsets are resolved against the pre-edit text.</summary>
	public void Apply(TextEdit edit) {
		ArgumentNullException.ThrowIfNull(edit.Text);
		var start = OffsetOf(edit.Range.Start);
		var end = OffsetOf(edit.Range.End);
		if (end < start) {
			throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edit));
		}

		var replacement = Normalize(edit.Text);
		Text = string.Concat(Text.AsSpan(0, start), replacement, Text.AsSpan(end));
		_lineStarts = ComputeLineStarts(Text);
	}

	/// <summary>
	/// Applies several edits as one transaction. Edits must be non-overlapping; they are
	/// applied bottom-up (last position first) so earlier offsets stay valid mid-batch.
	/// </summary>
	public void Apply(IReadOnlyList<TextEdit> edits) {
		ArgumentNullException.ThrowIfNull(edits);
		if (edits.Count == 0) {
			return;
		}

		var resolved = new (int Start, int End, string Text)[edits.Count];
		for (var i = 0; i < edits.Count; i++) {
			var edit = edits[i];
			ArgumentNullException.ThrowIfNull(edit.Text);
			var start = OffsetOf(edit.Range.Start);
			var end = OffsetOf(edit.Range.End);
			if (end < start) {
				throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edits));
			}

			resolved[i] = (start, end, Normalize(edit.Text));
		}

		Array.Sort(resolved, static (a, b) => a.Start.CompareTo(b.Start));
		for (var i = 1; i < resolved.Length; i++) {
			if (resolved[i].Start < resolved[i - 1].End) {
				throw new ArgumentException("Overlapping edits are not allowed in a single batch.", nameof(edits));
			}
		}

		var sb = new StringBuilder(Text.Length);
		var cursor = 0;
		foreach (var (start, end, text) in resolved) {
			sb.Append(Text, cursor, start - cursor);
			sb.Append(text);
			cursor = end;
		}

		sb.Append(Text, cursor, Text.Length - cursor);
		Text = sb.ToString();
		_lineStarts = ComputeLineStarts(Text);
	}

	/// <summary>The position just past the last character (last line, column after its final character).</summary>
	public Position EndPosition {
		get {
			var lastLineStart = _lineStarts[^1];
			var lastLineLength = Text.Length - lastLineStart;
			return new Position(_lineStarts.Length, lastLineLength + 1);
		}
	}

	private int OffsetOf(Position position) {
		if (position.LineNumber < 1 || position.LineNumber > _lineStarts.Length) {
			throw new ArgumentOutOfRangeException(
				nameof(position),
				position,
				$"Line {position.LineNumber} is outside [1, {_lineStarts.Length}].");
		}

		var lineStart = _lineStarts[position.LineNumber - 1];
		var lineEnd = position.LineNumber < _lineStarts.Length
			? _lineStarts[position.LineNumber] - 1 // exclude the '\n'
			: Text.Length;
		var lineLength = lineEnd - lineStart;

		if (position.Column < 1 || position.Column > lineLength + 1) {
			throw new ArgumentOutOfRangeException(
				nameof(position),
				position,
				$"Column {position.Column} is outside [1, {lineLength + 1}] on line {position.LineNumber}.");
		}

		return lineStart + (position.Column - 1);
	}

	private static int[] ComputeLineStarts(string text) {
		var starts = new List<int>(16) { 0 };
		for (var i = 0; i < text.Length; i++) {
			if (text[i] == '\n') {
				starts.Add(i + 1);
			}
		}

		return [.. starts];
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
