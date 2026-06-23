using System.Text;

namespace Weavie.Core.Documents;

/// <summary>
/// A minimal text buffer with Monaco line/column semantics and just the range math the document-model
/// interface needs. Line endings are normalized to "\n" on input. A substitute for Monaco, not a reimpl.
/// </summary>
public sealed class TextBuffer {
	// 0-based char offset of each line start. Rebuilt on mutation.
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
		int start = OffsetOf(range.Start);
		int end = OffsetOf(range.End);
		if (end < start) {
			throw new ArgumentException($"Range end {range.End} precedes start {range.Start}.", nameof(range));
		}

		return Text[start..end];
	}

	/// <summary>Applies a single edit. Offsets are resolved against the pre-edit text.</summary>
	public void Apply(TextEdit edit) {
		ArgumentNullException.ThrowIfNull(edit.Text);
		int start = OffsetOf(edit.Range.Start);
		int end = OffsetOf(edit.Range.End);
		if (end < start) {
			throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edit));
		}

		string replacement = Normalize(edit.Text);
		Text = string.Concat(Text.AsSpan(0, start), replacement, Text.AsSpan(end));
		_lineStarts = ComputeLineStarts(Text);
	}

	/// <summary>
	/// Applies several non-overlapping edits as one transaction, resolved against the pre-edit text.
	/// </summary>
	public void Apply(IReadOnlyList<TextEdit> edits) {
		ArgumentNullException.ThrowIfNull(edits);
		if (edits.Count == 0) {
			return;
		}

		var resolved = new (int Start, int End, string Text)[edits.Count];
		for (int i = 0; i < edits.Count; i++) {
			var edit = edits[i];
			ArgumentNullException.ThrowIfNull(edit.Text);
			int start = OffsetOf(edit.Range.Start);
			int end = OffsetOf(edit.Range.End);
			if (end < start) {
				throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edits));
			}

			resolved[i] = (start, end, Normalize(edit.Text));
		}

		Array.Sort(resolved, static (a, b) => a.Start.CompareTo(b.Start));
		for (int i = 1; i < resolved.Length; i++) {
			if (resolved[i].Start < resolved[i - 1].End) {
				throw new ArgumentException("Overlapping edits are not allowed in a single batch.", nameof(edits));
			}
		}

		var sb = new StringBuilder(Text.Length);
		int cursor = 0;
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
			int lastLineStart = _lineStarts[^1];
			int lastLineLength = Text.Length - lastLineStart;
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

		int lineStart = _lineStarts[position.LineNumber - 1];
		int lineEnd = position.LineNumber < _lineStarts.Length
			? _lineStarts[position.LineNumber] - 1 // exclude the '\n'
			: Text.Length;
		int lineLength = lineEnd - lineStart;

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
		for (int i = 0; i < text.Length; i++) {
			if (text[i] == '\n') {
				starts.Add(i + 1);
			}
		}

		return [.. starts];
	}

	private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
