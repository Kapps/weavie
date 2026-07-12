using System.Text;
using Weavie.Core.Changes;

namespace Weavie.Core.Corrections;

/// <summary>
/// Emits a compact unified diff (hunk headers + context lines, no file headers) so a correction stores as
/// one delta instead of doubling bytes with before/after text. Line endings are normalized first, so a
/// CRLF/LF-only difference is no correction at all. The line alignment comes from <see cref="LineHunker"/>.
/// </summary>
public static class CorrectionDiff {
	private const int Context = 3;

	/// <summary>
	/// The unified diff turning <paramref name="before"/> into <paramref name="after"/>; empty when they are
	/// line-for-line identical after EOL normalization.
	/// </summary>
	/// <param name="before">The baseline text (the agent's output).</param>
	/// <param name="after">The corrected text (the file's final content).</param>
	public static string Unified(string before, string after) {
		ArgumentNullException.ThrowIfNull(before);
		ArgumentNullException.ThrowIfNull(after);

		var ops = LineHunker.Align(LineDiff.SplitLines(before), LineDiff.SplitLines(after));
		if (ops.TrueForAll(op => op.Kind == LineHunker.LineOpKind.Equal)) {
			return string.Empty;
		}

		// Line numbers before each op: a-side counts non-inserts, b-side counts non-deletes.
		int[] aAt = new int[ops.Count + 1];
		int[] bAt = new int[ops.Count + 1];
		aAt[0] = bAt[0] = 1;
		for (int i = 0; i < ops.Count; i++) {
			aAt[i + 1] = aAt[i] + (ops[i].Kind == LineHunker.LineOpKind.Insert ? 0 : 1);
			bAt[i + 1] = bAt[i] + (ops[i].Kind == LineHunker.LineOpKind.Delete ? 0 : 1);
		}

		var sb = new StringBuilder();
		foreach (var (start, end) in HunkRanges(ops)) {
			int aCount = 0;
			int bCount = 0;
			for (int i = start; i < end; i++) {
				aCount += ops[i].Kind == LineHunker.LineOpKind.Insert ? 0 : 1;
				bCount += ops[i].Kind == LineHunker.LineOpKind.Delete ? 0 : 1;
			}

			// Unified-diff convention: a zero-count side anchors on the line BEFORE the hunk.
			sb.Append("@@ -").Append(aCount == 0 ? aAt[start] - 1 : aAt[start]).Append(',').Append(aCount)
				.Append(" +").Append(bCount == 0 ? bAt[start] - 1 : bAt[start]).Append(',').Append(bCount)
				.Append(" @@\n");
			for (int i = start; i < end; i++) {
				sb.Append(ops[i].Kind switch {
					LineHunker.LineOpKind.Delete => '-',
					LineHunker.LineOpKind.Insert => '+',
					_ => ' ',
				}).Append(ops[i].Text).Append('\n');
			}
		}

		return sb.ToString(0, sb.Length - 1); // drop the trailing '\n' so the delta round-trips as one JSON field
	}

	// Groups changed ops into hunk index ranges [start, end), each padded with up to Context equal lines;
	// changes separated by ≤ 2×Context equal lines merge into one hunk (their gap is all context anyway).
	private static List<(int Start, int End)> HunkRanges(List<LineHunker.LineOp> ops) {
		var ranges = new List<(int, int)>();
		int? first = null;
		int last = -1;
		for (int i = 0; i <= ops.Count; i++) {
			bool isChange = i < ops.Count && ops[i].Kind != LineHunker.LineOpKind.Equal;
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
}
