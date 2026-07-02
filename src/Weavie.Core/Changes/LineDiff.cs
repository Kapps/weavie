namespace Weavie.Core.Changes;

/// <summary>
/// Line-level added/removed counts between two texts, via the LCS of their lines. Line endings are normalized
/// first so CRLF/LF differences don't register as changes.
/// </summary>
public static class LineDiff {
	// Past this (rows × cols) the O(n·m) LCS gets too expensive; fall back to a coarse line-count delta.
	private const long LcsCellCap = 4_000_000;

	/// <summary>Returns the (added, removed) line counts turning <paramref name="before"/> into <paramref name="after"/>.</summary>
	/// <param name="before">The baseline text.</param>
	/// <param name="after">The current text.</param>
	public static (int Added, int Removed) Count(string before, string after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);

		string[] a = SplitLines(before);
		string[] b = SplitLines(after);

		if ((long)a.Length * b.Length > LcsCellCap) {
			int shared = Math.Min(a.Length, b.Length);
			return (Math.Max(0, b.Length - shared), Math.Max(0, a.Length - shared));
		}

		int common = LcsLength(a, b);
		return (b.Length - common, a.Length - common);
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

	private static string[] SplitLines(string text) =>
		text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');

	private static int LcsLength(string[] a, string[] b) {
		int[] previous = new int[b.Length + 1];
		int[] current = new int[b.Length + 1];

		for (int i = 1; i <= a.Length; i++) {
			for (int j = 1; j <= b.Length; j++) {
				current[j] = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal)
					? previous[j - 1] + 1
					: Math.Max(previous[j], current[j - 1]);
			}
			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}
}
