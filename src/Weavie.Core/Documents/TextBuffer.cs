using System.Text;

namespace Weavie.Core.Documents;

/// <summary>
/// A minimal text buffer implementing exactly the range math the document-model
/// interface needs, with Monaco line/column semantics. Line endings are normalized
/// to "\n" on input. This is deliberately tiny — it is a <em>substitute for</em>
/// Monaco in T1 tests, not a reimplementation of it (Editor &amp; Shared Models:
/// "keep the interface tiny so parity risk is bounded").
/// </summary>
public sealed class TextBuffer
{
    private string _text;
    // Start offset of each line (0-based char offsets into _text). Rebuilt on mutation.
    private int[] _lineStarts;

    public TextBuffer(string initialText)
    {
        ArgumentNullException.ThrowIfNull(initialText);
        _text = Normalize(initialText);
        _lineStarts = ComputeLineStarts(_text);
    }

    public string Text => _text;

    public int LineCount => _lineStarts.Length;

    public string GetText(TextRange range)
    {
        var start = OffsetOf(range.Start);
        var end = OffsetOf(range.End);
        if (end < start)
        {
            throw new ArgumentException($"Range end {range.End} precedes start {range.Start}.", nameof(range));
        }

        return _text[start..end];
    }

    /// <summary>Applies a single edit. Offsets are resolved against the pre-edit text.</summary>
    public void Apply(TextEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Text);
        var start = OffsetOf(edit.Range.Start);
        var end = OffsetOf(edit.Range.End);
        if (end < start)
        {
            throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edit));
        }

        var replacement = Normalize(edit.Text);
        _text = string.Concat(_text.AsSpan(0, start), replacement, _text.AsSpan(end));
        _lineStarts = ComputeLineStarts(_text);
    }

    /// <summary>
    /// Applies several edits as one transaction. Edits must be non-overlapping; they are
    /// applied bottom-up (last position first) so earlier offsets stay valid mid-batch.
    /// </summary>
    public void Apply(IReadOnlyList<TextEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            return;
        }

        var resolved = new (int Start, int End, string Text)[edits.Count];
        for (var i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            ArgumentNullException.ThrowIfNull(edit.Text);
            var start = OffsetOf(edit.Range.Start);
            var end = OffsetOf(edit.Range.End);
            if (end < start)
            {
                throw new ArgumentException($"Range end {edit.Range.End} precedes start {edit.Range.Start}.", nameof(edits));
            }

            resolved[i] = (start, end, Normalize(edit.Text));
        }

        Array.Sort(resolved, static (a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < resolved.Length; i++)
        {
            if (resolved[i].Start < resolved[i - 1].End)
            {
                throw new ArgumentException("Overlapping edits are not allowed in a single batch.", nameof(edits));
            }
        }

        var sb = new StringBuilder(_text.Length);
        var cursor = 0;
        foreach (var (start, end, text) in resolved)
        {
            sb.Append(_text, cursor, start - cursor);
            sb.Append(text);
            cursor = end;
        }

        sb.Append(_text, cursor, _text.Length - cursor);
        _text = sb.ToString();
        _lineStarts = ComputeLineStarts(_text);
    }

    public Position EndPosition
    {
        get
        {
            var lastLineStart = _lineStarts[^1];
            var lastLineLength = _text.Length - lastLineStart;
            return new Position(_lineStarts.Length, lastLineLength + 1);
        }
    }

    private int OffsetOf(Position position)
    {
        if (position.LineNumber < 1 || position.LineNumber > _lineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"Line {position.LineNumber} is outside [1, {_lineStarts.Length}].");
        }

        var lineStart = _lineStarts[position.LineNumber - 1];
        var lineEnd = position.LineNumber < _lineStarts.Length
            ? _lineStarts[position.LineNumber] - 1 // exclude the '\n'
            : _text.Length;
        var lineLength = lineEnd - lineStart;

        if (position.Column < 1 || position.Column > lineLength + 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"Column {position.Column} is outside [1, {lineLength + 1}] on line {position.LineNumber}.");
        }

        return lineStart + (position.Column - 1);
    }

    private static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int>(16) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return [.. starts];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
