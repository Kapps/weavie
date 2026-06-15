namespace Weavie.Core.Documents;

/// <summary>
/// A caret position using Monaco/LSP-on-the-wire conventions: 1-based line,
/// 1-based column where column 1 is before the first character and column
/// (lineLength + 1) is after the last.
/// </summary>
public readonly record struct Position(int LineNumber, int Column)
{
    public static readonly Position Start = new(1, 1);
}

/// <summary>A half-open range [Start, End): End is exclusive, matching Monaco.</summary>
public readonly record struct TextRange(Position Start, Position End)
{
    public static TextRange Collapsed(Position at) => new(at, at);

    public bool IsEmpty => Start == End;
}

/// <summary>
/// A structured edit: replace everything in <see cref="Range"/> with <see cref="Text"/>.
/// Insertion is an empty range; deletion is empty text. This is the only mutation
/// primitive the document model exposes (see Headless &amp; Testing: "apply a structured edit").
/// </summary>
public readonly record struct TextEdit(TextRange Range, string Text)
{
    public static TextEdit Insert(Position at, string text) => new(TextRange.Collapsed(at), text);

    public static TextEdit Replace(TextRange range, string text) => new(range, text);
}
