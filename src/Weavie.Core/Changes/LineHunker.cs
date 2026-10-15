using System.Runtime.InteropServices;

namespace Weavie.Core.Changes;

/// <summary>A changed region between two texts: its line range on each side (1-based, end-exclusive).</summary>
/// <param name="BeforeRange">The range on the <c>before</c> side; empty (Start == EndExclusive) for a pure insertion.</param>
/// <param name="AfterRange">The range on the <c>after</c> side; empty for a pure deletion.</param>
public readonly record struct LineHunk(LineRange BeforeRange, LineRange AfterRange);

/// <summary>
/// The line-level alignment of two texts. <see cref="Hunks"/> groups their differences into hunks that carry
/// each side's range; <see cref="Align"/> is the shared alignment the correction diff also builds on. Callers split
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

	// The ordered Equal/Delete/Insert alignment: trim the common prefix/suffix, then align the middle by patience-anchored Myers,
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
		int ai = aStart;
		int bi = bStart;
		foreach (var (matchA, matchB) in Matches(a, b, aStart, aCount, bStart, bCount)) {
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

	// Patience-anchored Myers diff: unique lines anchor the alignment and Myers' O((N+M)·D) fills the gaps, so huge
	// files stay cheap under scattered edits, reorders, and rewrites. Lines absent from the other side never match.
	private static List<(int A, int B)> Matches(
		IReadOnlyList<string> a,
		IReadOnlyList<string> b,
		int aStart,
		int aCount,
		int bStart,
		int bCount) {
		var ids = new Dictionary<string, int>(StringComparer.Ordinal);
		int[] aIds = Intern(ids, a, aStart, aCount);
		int[] bIds = Intern(ids, b, bStart, bCount);
		var inA = aIds.ToHashSet();
		var inB = bIds.ToHashSet();
		int[] aKept = [.. Enumerable.Range(0, aCount).Where(i => inB.Contains(aIds[i]))];
		int[] bKept = [.. Enumerable.Range(0, bCount).Where(j => inA.Contains(bIds[j]))];
		int[] x = [.. aKept.Select(i => aIds[i])];
		int[] y = [.. bKept.Select(j => bIds[j])];
		var matches = new List<(int A, int B)>();
		Diff(matches, x, y, 0, x.Length, 0, y.Length);
		return matches.ConvertAll(match => (aStart + aKept[match.A], bStart + bKept[match.B]));
	}

	private static int[] Intern(Dictionary<string, int> ids, IReadOnlyList<string> lines, int start, int count) {
		int[] result = new int[count];
		for (int i = 0; i < count; i++) {
			ref int id = ref CollectionsMarshal.GetValueRefOrAddDefault(ids, lines[start + i], out bool exists);
			if (!exists) {
				id = ids.Count;
			}
			result[i] = id;
		}
		return result;
	}

	private static void Diff(List<(int A, int B)> matches, int[] a, int[] b, int aLo, int aHi, int bLo, int bHi) {
		while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo]) {
			matches.Add((aLo++, bLo++));
		}
		int suffix = 0;
		while (aLo < aHi - suffix && bLo < bHi - suffix && a[aHi - 1 - suffix] == b[bHi - 1 - suffix]) {
			suffix++;
		}
		aHi -= suffix;
		bHi -= suffix;
		if (aHi - aLo == 1 || bHi - bLo == 1) {
			for (int i = aLo; i < aHi; i++) {
				for (int j = bLo; j < bHi; j++) {
					if (a[i] == b[j]) {
						matches.Add((i, j));
						i = aHi;
						break;
					}
				}
			}
		} else if (aLo < aHi && bLo < bHi) {
			var anchors = Anchors(a, b, aLo, aHi, bLo, bHi);
			if (anchors.Count > 0) {
				foreach (var (x, y) in anchors) {
					Diff(matches, a, b, aLo, x, bLo, y);
					matches.Add((x, y));
					(aLo, bLo) = (x + 1, y + 1);
				}
				Diff(matches, a, b, aLo, aHi, bLo, bHi);
			} else if (TrySplit(a, b, aLo, aHi, bLo, bHi, out int x, out int y)) {
				Diff(matches, a, b, aLo, x, bLo, y);
				Diff(matches, a, b, x, aHi, y, bHi);
			}
		}
		for (int i = 0; i < suffix; i++) {
			matches.Add((aHi + i, bHi + i));
		}
	}

	// Patience anchors: the longest in-order run of lines occurring exactly once on each side. Aligning on them first keeps
	// reordered generated code near-linear, where Myers alone degrades as repeated lines like "}" inflate D.
	private static List<(int A, int B)> Anchors(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi) {
		var counts = new Dictionary<int, (int A, int B, int BIndex)>();
		for (int i = aLo; i < aHi; i++) {
			CollectionsMarshal.GetValueRefOrAddDefault(counts, a[i], out _).A++;
		}
		for (int j = bLo; j < bHi; j++) {
			ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(counts, b[j], out _);
			count.B++;
			count.BIndex = j;
		}
		var unique = new List<(int A, int B)>();
		for (int i = aLo; i < aHi; i++) {
			if (counts[a[i]] is (1, 1, int j)) {
				unique.Add((i, j));
			}
		}
		var tails = new List<int>();
		int[] previous = new int[unique.Count];
		for (int k = 0; k < unique.Count; k++) {
			int lo = 0;
			int hi = tails.Count;
			while (lo < hi) {
				int mid = (lo + hi) / 2;
				if (unique[tails[mid]].B < unique[k].B) lo = mid + 1;
				else hi = mid;
			}
			previous[k] = lo > 0 ? tails[lo - 1] : -1;
			if (lo == tails.Count) tails.Add(k);
			else tails[lo] = k;
		}
		var anchors = new List<(int A, int B)>(tails.Count);
		for (int k = tails.Count > 0 ? tails[^1] : -1; k >= 0; k = previous[k]) {
			anchors.Add(unique[k]);
		}
		anchors.Reverse();
		return anchors;
	}

	// Finds where the forward and reverse D-paths meet (the "middle snake"); false when the ranges share no line. Past
	// git xdiff's cost bound it splits at the furthest forward point instead: still a valid diff, no longer minimal.
	private static bool TrySplit(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi, out int x, out int y) {
		int n = aHi - aLo;
		int m = bHi - bLo;
		int maxD = (n + m + 1) / 2;
		int limit = Math.Min(maxD, Math.Max(256, (int)Math.Sqrt(n + m)));
		int offset = limit;
		int[] forward = new int[2 * limit];
		int[] reverse = new int[2 * limit];
		Array.Fill(forward, -1);
		Array.Fill(reverse, -1);
		forward[offset + 1] = 0;
		reverse[offset + 1] = 0;
		int delta = n - m;
		bool front = delta % 2 != 0;
		int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;
		for (int d = 0; d < limit; d++) {
			for (int k1 = -d + k1Start; k1 <= d - k1End; k1 += 2) {
				int k1Offset = offset + k1;
				int x1 = k1 == -d || (k1 != d && forward[k1Offset - 1] < forward[k1Offset + 1])
					? forward[k1Offset + 1] : forward[k1Offset - 1] + 1;
				int y1 = x1 - k1;
				while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1]) {
					x1++;
					y1++;
				}
				forward[k1Offset] = x1;
				if (x1 > n) {
					k1End += 2;
				} else if (y1 > m) {
					k1Start += 2;
				} else if (front) {
					int k2Offset = offset + delta - k1;
					if (k2Offset >= 0 && k2Offset < reverse.Length && reverse[k2Offset] != -1 && x1 >= n - reverse[k2Offset]) {
						(x, y) = (aLo + x1, bLo + y1);
						return true;
					}
				}
			}
			for (int k2 = -d + k2Start; k2 <= d - k2End; k2 += 2) {
				int k2Offset = offset + k2;
				int x2 = k2 == -d || (k2 != d && reverse[k2Offset - 1] < reverse[k2Offset + 1])
					? reverse[k2Offset + 1] : reverse[k2Offset - 1] + 1;
				int y2 = x2 - k2;
				while (x2 < n && y2 < m && a[aHi - 1 - x2] == b[bHi - 1 - y2]) {
					x2++;
					y2++;
				}
				reverse[k2Offset] = x2;
				if (x2 > n) {
					k2End += 2;
				} else if (y2 > m) {
					k2Start += 2;
				} else if (!front) {
					int k1Offset = offset + delta - k2;
					if (k1Offset >= 0 && k1Offset < forward.Length && forward[k1Offset] != -1 && forward[k1Offset] >= n - x2) {
						int x1 = forward[k1Offset];
						(x, y) = (aLo + x1, bLo + offset + x1 - k1Offset);
						return true;
					}
				}
			}
		}
		(x, y) = (aLo, bLo);
		for (int k = 1 - limit; limit < maxD && k < limit; k++) {
			int fx = forward[offset + k];
			if (fx >= 0 && fx <= n && fx - k >= 0 && fx - k <= m && 2 * fx - k > x + y - aLo - bLo && 2 * fx - k < n + m)
				(x, y) = (aLo + fx, bLo + fx - k);
		}
		return x + y > aLo + bLo;
	}
}
