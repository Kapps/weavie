namespace Weavie.Core.Changes;

/// <summary>
/// Line-level added/removed counts between two texts, via the length of their longest common subsequence of
/// lines. Line endings are normalized first, so CRLF/LF differences don't register as changes. Used for the
/// session changes summary; the full visual diff is rendered by the editor's diff view.
/// </summary>
public static class LineDiff {
	// Above this (rows × cols) the O(n·m) LCS is too costly; fall back to a coarse line-count delta.
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
