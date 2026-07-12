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

		AppendLcs(ops, a, b, prefix, a.Count - prefix - suffix, prefix, b.Count - prefix - suffix);

		for (int i = 0; i < suffix; i++) {
			ops.Add(new LineOp(LineOpKind.Equal, a[a.Count - suffix + i]));
		}

		return ops;
	}

	private static void AppendLcs(
		List<LineOp> ops,
		IReadOnlyList<string> a,
		IReadOnlyList<string> b,
		int aStart,
		int aCount,
		int bStart,
		int bCount) {
		var matches = new List<(int A, int B)>();
		FindMatches(matches, a, b, aStart, aCount, bStart, bCount);
		int ai = aStart;
		int bi = bStart;
		foreach (var (matchA, matchB) in matches) {
			while (ai < matchA) {
				ops.Add(new LineOp(LineOpKind.Delete, a[ai++]));
			}
			while (bi < matchB) {
				ops.Add(new LineOp(LineOpKind.Insert, b[bi++]));
			}
			ops.Add(new LineOp(LineOpKind.Equal, a[ai]));
			ai++;
			bi++;
		}
		while (ai < aStart + aCount) {
			ops.Add(new LineOp(LineOpKind.Delete, a[ai++]));
		}
		while (bi < bStart + bCount) {
			ops.Add(new LineOp(LineOpKind.Insert, b[bi++]));
		}
	}

	// Hirschberg's exact LCS uses linear memory, so large files never broaden provenance through a coarse fallback.
	private static void FindMatches(
		List<(int A, int B)> matches,
		IReadOnlyList<string> a,
		IReadOnlyList<string> b,
		int aStart,
		int aCount,
		int bStart,
		int bCount) {
		if (aCount == 0 || bCount == 0) {
			return;
		}
		if (aCount == 1) {
			for (int j = 0; j < bCount; j++) {
				if (string.Equals(a[aStart], b[bStart + j], StringComparison.Ordinal)) {
					matches.Add((aStart, bStart + j));
					return;
				}
			}
			return;
		}

		int leftCount = aCount / 2;
		int[] left = LcsLengths(a, b, aStart, leftCount, bStart, bCount, reverse: false);
		int[] right = LcsLengths(a, b, aStart + leftCount, aCount - leftCount, bStart, bCount, reverse: true);
		int split = 0;
		for (int j = 1; j <= bCount; j++) {
			if (left[j] + right[bCount - j] > left[split] + right[bCount - split]) {
				split = j;
			}
		}

		FindMatches(matches, a, b, aStart, leftCount, bStart, split);
		FindMatches(matches, a, b, aStart + leftCount, aCount - leftCount, bStart + split, bCount - split);
	}

	private static int[] LcsLengths(
		IReadOnlyList<string> a,
		IReadOnlyList<string> b,
		int aStart,
		int aCount,
		int bStart,
		int bCount,
		bool reverse) {
		int[] previous = new int[bCount + 1];
		int[] current = new int[bCount + 1];
		for (int i = 0; i < aCount; i++) {
			string aLine = a[reverse ? aStart + aCount - 1 - i : aStart + i];
			for (int j = 0; j < bCount; j++) {
				string bLine = b[reverse ? bStart + bCount - 1 - j : bStart + j];
				current[j + 1] = string.Equals(aLine, bLine, StringComparison.Ordinal)
					? previous[j] + 1
					: Math.Max(previous[j + 1], current[j]);
			}
			(previous, current) = (current, previous);
			Array.Clear(current);
		}
		return previous;
	}
}
