namespace Weavie.Core.Spelling;

/// <summary>A misspelled word's zero-based UTF-16 range in the source text.</summary>
public readonly record struct SpellIssue(int Start, int Length, string Word);

/// <summary>Raised when a custom dictionary cannot be parsed or persisted.</summary>
public sealed class SpellDictionaryException : Exception {
	/// <summary>Creates an exception describing the failed dictionary operation.</summary>
	public SpellDictionaryException(string message, Exception? innerException) : base(message, innerException) {
	}
}
