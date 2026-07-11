using System.Text;

namespace Weavie.Core.Corrections;

/// <summary>
/// Emits a compact unified diff (hunk headers + context lines, no file headers) so a correction stores as
/// one delta instead of doubling bytes with before/after text. Line endings are normalized first, so a
/// CRLF/LF-only difference is no correction at all.
/// </summary>
public static class CorrectionDiff {
	private const int Context = 3;
	// Past this (rows × cols, after common prefix/suffix trimming) the O(n·m) LCS table gets too expensive;
	// fall back to one coarse delete-all/insert-all hunk — the per-file byte cap truncates it anyway.
	private const long LcsCellCap = 1_000_000;

	private enum Kind { Equal, Delete, Insert }

	private readonly record struct Op(Kind Kind, string Text);

	/// <summary>
	/// The unified diff turning <paramref name="before"/> into <paramref name="after"/>; empty when they are
	/// line-for-line identical after EOL normalization.
	/// </summary>
	/// <param name="before">The baseline text (the agent's output).</param>
	/// <param name="after">The corrected text (the file's final content).</param>
	public static string Unified(string before, string after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);

		var ops = Ops(SplitLines(before), SplitLines(after));
		if (ops.TrueForAll(op => op.Kind == Kind.Equal)) {
			return string.Empty;
		}

		// Line numbers before each op: a-side counts non-inserts, b-side counts non-deletes.
		int[] aAt = new int[ops.Count + 1];
		int[] bAt = new int[ops.Count + 1];
		aAt[0] = bAt[0] = 1;
		for (int i = 0; i < ops.Count; i++) {
			aAt[i + 1] = aAt[i] + (ops[i].Kind == Kind.Insert ? 0 : 1);
			bAt[i + 1] = bAt[i] + (ops[i].Kind == Kind.Delete ? 0 : 1);
		}

		var sb = new StringBuilder();
		foreach (var (start, end) in HunkRanges(ops)) {
			int aCount = 0;
			int bCount = 0;
			for (int i = start; i < end; i++) {
				aCount += ops[i].Kind == Kind.Insert ? 0 : 1;
				bCount += ops[i].Kind == Kind.Delete ? 0 : 1;
			}

			// Unified-diff convention: a zero-count side anchors on the line BEFORE the hunk.
			sb.Append("@@ -").Append(aCount == 0 ? aAt[start] - 1 : aAt[start]).Append(',').Append(aCount)
				.Append(" +").Append(bCount == 0 ? bAt[start] - 1 : bAt[start]).Append(',').Append(bCount)
				.Append(" @@\n");
			for (int i = start; i < end; i++) {
				sb.Append(ops[i].Kind switch { Kind.Delete => '-', Kind.Insert => '+', _ => ' ' })
					.Append(ops[i].Text).Append('\n');
			}
		}

		return sb.ToString(0, sb.Length - 1); // drop the trailing '\n' so the delta round-trips as one JSON field
	}

	// Groups changed ops into hunk index ranges [start, end), each padded with up to Context equal lines;
	// changes separated by ≤ 2×Context equal lines merge into one hunk (their gap is all context anyway).
	private static List<(int Start, int End)> HunkRanges(List<Op> ops) {
		var ranges = new List<(int, int)>();
		int? first = null;
		int last = -1;
		for (int i = 0; i <= ops.Count; i++) {
			bool isChange = i < ops.Count && ops[i].Kind != Kind.Equal;
			if (isChange) {
				if (first is null || i - last - 1 > 2 * Context) {
					if (first is { } begun) {
						ranges.Add((Math.Max(0, begun - Context), Math.Min(ops.Count, last + 1 + Context)));
					}

					first = i;
				}

				last = i;
			} else if (i == ops.Count && first is { } begun) {
				ranges.Add((Math.Max(0, begun - Context), Math.Min(ops.Count, last + 1 + Context)));
			}
		}

		return ranges;
	}

	private static List<Op> Ops(string[] a, string[] b) {
		int prefix = 0;
		while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal)) {
			prefix++;
		}

		int suffix = 0;
		while (suffix < a.Length - prefix && suffix < b.Length - prefix
			&& string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal)) {
			suffix++;
		}

		var ops = new List<Op>(Math.Max(a.Length, b.Length));
		for (int i = 0; i < prefix; i++) {
			ops.Add(new Op(Kind.Equal, a[i]));
		}

		int midA = a.Length - prefix - suffix;
		int midB = b.Length - prefix - suffix;
		if ((long)midA * midB > LcsCellCap) {
			for (int i = 0; i < midA; i++) {
				ops.Add(new Op(Kind.Delete, a[prefix + i]));
			}

			for (int j = 0; j < midB; j++) {
				ops.Add(new Op(Kind.Insert, b[prefix + j]));
			}
		} else {
			AppendLcsOps(ops, a, b, prefix, midA, midB);
		}

		for (int i = 0; i < suffix; i++) {
			ops.Add(new Op(Kind.Equal, a[a.Length - suffix + i]));
		}

		return ops;
	}

	// Standard LCS table + backtrack over the trimmed middle, preferring deletes before inserts at ties so
	// each change block reads -old then +new.
	private static void AppendLcsOps(List<Op> ops, string[] a, string[] b, int offset, int n, int m) {
		int[,] table = new int[n + 1, m + 1];
		for (int i = 1; i <= n; i++) {
			for (int j = 1; j <= m; j++) {
				table[i, j] = string.Equals(a[offset + i - 1], b[offset + j - 1], StringComparison.Ordinal)
					? table[i - 1, j - 1] + 1
					: Math.Max(table[i - 1, j], table[i, j - 1]);
			}
		}

		var reversed = new List<Op>(n + m);
		int x = n;
		int y = m;
		while (x > 0 || y > 0) {
			if (x > 0 && y > 0 && string.Equals(a[offset + x - 1], b[offset + y - 1], StringComparison.Ordinal)) {
				reversed.Add(new Op(Kind.Equal, a[offset + x - 1]));
				x--;
				y--;
			} else if (y > 0 && (x == 0 || table[x, y - 1] >= table[x - 1, y])) {
				reversed.Add(new Op(Kind.Insert, b[offset + y - 1]));
				y--;
			} else {
				reversed.Add(new Op(Kind.Delete, a[offset + x - 1]));
				x--;
			}
		}

		reversed.Reverse();
		ops.AddRange(reversed);
	}

	private static string[] SplitLines(string text) =>
		text.Length == 0 ? [] : text.ReplaceLineEndings("\n").Split('\n');
}
