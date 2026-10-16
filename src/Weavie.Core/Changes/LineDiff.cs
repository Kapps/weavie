namespace Weavie.Core.Changes;

/// <summary>
/// Line-level added/removed counts between two texts, via the LCS of their lines. Line endings are normalized
/// first so CRLF/LF differences don't register as changes.
/// </summary>
public static class LineDiff {
	/// <summary>Returns the (added, removed) line counts turning <paramref name="before"/> into <paramref name="after"/>.</summary>
	/// <param name="before">The baseline text.</param>
	/// <param name="after">The current text.</param>
	public static (int Added, int Removed) Count(string before, string after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);
		var ops = LineHunker.Align(SplitLines(before), SplitLines(after));
		return (ops.Count(op => op.Kind == LineHunker.LineOpKind.Insert), ops.Count(op => op.Kind == LineHunker.LineOpKind.Delete));
	}

	/// <summary>
	/// The 1-based number of the first differing line — a "jump to the edit" target, clamped into
	/// <paramref name="after"/>'s range. <see langword="null"/> when the texts are line-for-line identical.
	/// </summary>
	/// <param name="before">The baseline text (before the edit).</param>
	/// <param name="after">The current text (after the edit).</param>
	public static int? FirstChangedLine(string before, string after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);

		string[] a = SplitLines(before);
		string[] b = SplitLines(after);

		int shared = Math.Min(a.Length, b.Length);
		for (int i = 0; i < shared; i++) {
			if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) {
				return i + 1;
			}
		}

		// Common prefix is identical: the change is a trailing insertion or deletion (or nothing).
		if (a.Length == b.Length) {
			return null;
		}

		return Math.Clamp(shared + 1, 1, Math.Max(b.Length, 1));
	}

	/// <summary>Splits <paramref name="text"/> into lines, normalizing every EOL style to <c>\n</c> first (empty text → no lines).</summary>
	/// <param name="text">The text to split.</param>
	internal static string[] SplitLines(string text) =>
		text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');
}
