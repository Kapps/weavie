namespace Weavie.Core.Spelling;

/// <summary>A misspelled word's zero-based UTF-16 range in the source text.</summary>
public readonly record struct SpellIssue(int Start, int Length, string Word);

/// <summary>A line submitted for spell checking, identified by the editor's stable anchor.</summary>
public readonly record struct SpellCheckLine(string AnchorId, string Text);

/// <summary>The issues returned for one <see cref="SpellCheckLine"/>.</summary>
public readonly record struct SpellCheckLineResult(string AnchorId, IReadOnlyList<SpellIssue> Issues);

/// <summary>Raised when a custom dictionary cannot be parsed or persisted.</summary>
public sealed class SpellDictionaryException : Exception {
	/// <summary>Creates an exception describing the failed dictionary operation.</summary>
	public SpellDictionaryException(string message, Exception? innerException) : base(message, innerException) {
	}
}
