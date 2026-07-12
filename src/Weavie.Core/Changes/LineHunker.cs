namespace Weavie.Core.Changes;

/// <summary>A changed region between two texts: its line range on each side (1-based, end-exclusive).</summary>
/// <param name="BeforeRange">The range on the <c>before</c> side; empty (Start == EndExclusive) for a pure insertion.</param>
/// <param name="AfterRange">The range on the <c>after</c> side; empty for a pure deletion.</param>
public readonly record struct LineHunk(LineRange BeforeRange, LineRange AfterRange);

/// <summary>
/// The line-level alignment of two texts. <see cref="Hunks"/> groups their differences into hunks that carry
/// each side's range; <see cref="Align"/> is the shared LCS the correction diff also builds on. Callers split
/// into lines themselves so the ranges line up with whatever slicing they do next.
/// </summary>
public static class LineHunker {
	internal enum LineOpKind { Equal, Delete, Insert }

	internal readonly record struct LineOp(LineOpKind Kind, string Text);

	// Past this (rows × cols, after common prefix/suffix trimming) the O(n·m) LCS table gets too expensive; fall
	// back to one coarse delete-all/insert-all block, matching CorrectionDiff (which builds on the same Align).
	private const long LcsCellCap = 1_000_000;

	/// <summary>
	/// The hunks turning <paramref name="before"/> into <paramref name="after"/>, in order; empty when they are
	/// line-for-line identical. Each hunk's <see cref="LineHunk.BeforeRange"/>/<see cref="LineHunk.AfterRange"/>
	/// are 1-based, end-exclusive, and a zero-length range marks a pure insertion (before) or deletion (after).
	/// </summary>
	/// <param name="before">The before-side lines.</param>
	/// <param name="after">The after-side lines.</param>
	public static IReadOnlyList<LineHunk> Hunks(IReadOnlyList<string> before, IReadOnlyList<string> after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);
		var hunks = new List<LineHunk>();
		int a = 1;
		int b = 1;
		int aStart = 0;
		int bStart = 0;
		bool inHunk = false;
		foreach (var op in Align(before, after)) {
			if (op.Kind == LineOpKind.Equal) {
				if (inHunk) {
					hunks.Add(new LineHunk(new LineRange(aStart, a), new LineRange(bStart, b)));
					inHunk = false;
				}

				a++;
				b++;
				continue;
			}

			if (!inHunk) {
				aStart = a;
				bStart = b;
				inHunk = true;
			}

			if (op.Kind == LineOpKind.Delete) {
				a++;
			} else {
				b++;
			}
		}

		if (inHunk) {
			hunks.Add(new LineHunk(new LineRange(aStart, a), new LineRange(bStart, b)));
		}

		return hunks;
	}

	// The ordered Equal/Delete/Insert alignment: trim the common prefix/suffix, then LCS-backtrack the middle,
	// preferring deletes before inserts at ties so each change block reads -old then +new. Shared with CorrectionDiff.
	internal static List<LineOp> Align(IReadOnlyList<string> a, IReadOnlyList<string> b) {
		int prefix = 0;
		while (prefix < a.Count && prefix < b.Count && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal)) {
			prefix++;
		}

		int suffix = 0;
		while (suffix < a.Count - prefix && suffix < b.Count - prefix
			&& string.Equals(a[a.Count - 1 - suffix], b[b.Count - 1 - suffix], StringComparison.Ordinal)) {
			suffix++;
		}

		var ops = new List<LineOp>(Math.Max(a.Count, b.Count));
		for (int i = 0; i < prefix; i++) {
			ops.Add(new LineOp(LineOpKind.Equal, a[i]));
		}

		int midA = a.Count - prefix - suffix;
		int midB = b.Count - prefix - suffix;
		if ((long)midA * midB > LcsCellCap) {
			for (int i = 0; i < midA; i++) {
				ops.Add(new LineOp(LineOpKind.Delete, a[prefix + i]));
			}

			for (int j = 0; j < midB; j++) {
				ops.Add(new LineOp(LineOpKind.Insert, b[prefix + j]));
			}
		} else {
			AppendLcs(ops, a, b, prefix, midA, midB);
		}

		for (int i = 0; i < suffix; i++) {
			ops.Add(new LineOp(LineOpKind.Equal, a[a.Count - suffix + i]));
		}

		return ops;
	}

	private static void AppendLcs(List<LineOp> ops, IReadOnlyList<string> a, IReadOnlyList<string> b, int offset, int n, int m) {
		int[,] table = new int[n + 1, m + 1];
		for (int i = 1; i <= n; i++) {
			for (int j = 1; j <= m; j++) {
				table[i, j] = string.Equals(a[offset + i - 1], b[offset + j - 1], StringComparison.Ordinal)
					? table[i - 1, j - 1] + 1
					: Math.Max(table[i - 1, j], table[i, j - 1]);
			}
		}

		var reversed = new List<LineOp>(n + m);
		int x = n;
		int y = m;
		while (x > 0 || y > 0) {
			if (x > 0 && y > 0 && string.Equals(a[offset + x - 1], b[offset + y - 1], StringComparison.Ordinal)) {
				reversed.Add(new LineOp(LineOpKind.Equal, a[offset + x - 1]));
				x--;
				y--;
			} else if (y > 0 && (x == 0 || table[x, y - 1] >= table[x - 1, y])) {
				reversed.Add(new LineOp(LineOpKind.Insert, b[offset + y - 1]));
				y--;
			} else {
				reversed.Add(new LineOp(LineOpKind.Delete, a[offset + x - 1]));
				x--;
			}
		}

		reversed.Reverse();
		ops.AddRange(reversed);
	}
}
