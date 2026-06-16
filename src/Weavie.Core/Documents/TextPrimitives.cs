namespace Weavie.Core.Documents;

/// <summary>
/// A caret position using Monaco/LSP-on-the-wire conventions: 1-based line,
/// 1-based column where column 1 is before the first character and column
/// (lineLength + 1) is after the last.
/// </summary>
public readonly record struct Position(int LineNumber, int Column) {
	/// <summary>The position before the first character of the document (line 1, column 1).</summary>
	public static readonly Position Start = new(1, 1);
}

/// <summary>A half-open range [Start, End): End is exclusive, matching Monaco.</summary>
public readonly record struct TextRange(Position Start, Position End) {
	/// <summary>Creates an empty range positioned at <paramref name="at"/> (start equals end).</summary>
	public static TextRange Collapsed(Position at) => new(at, at);

	/// <summary>True when the range covers no characters (start equals end).</summary>
	public bool IsEmpty => Start == End;
}

/// <summary>
/// A structured edit: replace everything in <see cref="Range"/> with <see cref="Text"/>.
/// Insertion is an empty range; deletion is empty text. This is the only mutation
/// primitive the document model exposes (see Headless &amp; Testing: "apply a structured edit").
/// </summary>
public readonly record struct TextEdit(TextRange Range, string Text) {
	/// <summary>Builds an insertion edit: inserts <paramref name="text"/> at <paramref name="at"/> without removing anything.</summary>
	public static TextEdit Insert(Position at, string text) => new(TextRange.Collapsed(at), text);

	/// <summary>Builds a replacement edit: replaces the contents of <paramref name="range"/> with <paramref name="text"/>.</summary>
	public static TextEdit Replace(TextRange range, string text) => new(range, text);
}
