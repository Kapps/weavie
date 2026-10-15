using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Core.Tests.Changes;

/// <summary>Exercises <see cref="LineHunker"/>: change grouping with both-side ranges, and range overlap.</summary>
public sealed class LineHunkerTests {
	[Fact]
	public void Identical_NoHunks() =>
		Assert.Empty(LineHunker.Hunks(["a", "b", "c"], ["a", "b", "c"]));

	[Fact]
	public void SingleLineChange_OneHunkOnBothSides() {
		var hunk = Assert.Single(LineHunker.Hunks(["a", "b", "c"], ["a", "B", "c"]));
		Assert.Equal(new LineRange(2, 3), hunk.BeforeRange);
		Assert.Equal(new LineRange(2, 3), hunk.AfterRange);
	}

	[Fact]
	public void PureInsertion_HasEmptyBeforeRange() {
		var hunk = Assert.Single(LineHunker.Hunks(["a", "b"], ["a", "NEW", "b"]));
		Assert.Equal(hunk.BeforeRange.Start, hunk.BeforeRange.EndExclusive);
		Assert.Equal(new LineRange(2, 3), hunk.AfterRange);
	}

	[Fact]
	public void PureDeletion_HasEmptyAfterRange() {
		var hunk = Assert.Single(LineHunker.Hunks(["a", "gone", "b"], ["a", "b"]));
		Assert.Equal(new LineRange(2, 3), hunk.BeforeRange);
		Assert.Equal(hunk.AfterRange.Start, hunk.AfterRange.EndExclusive);
	}

	[Fact]
	public void FarApartChanges_ProduceTwoHunks() =>
		Assert.Equal(2, LineHunker.Hunks(["a", "b", "c", "d", "e"], ["A", "b", "c", "d", "E"]).Count);

	[Fact]
	public void FarApartChanges_AboveFormerCellCap_RemainSeparate() {
		string[] before = [.. Enumerable.Range(0, 1_100).Select(i => $"line {i}")];
		string[] after = (string[])before.Clone();
		after[0] = "FIRST";
		after[^1] = "LAST";

		Assert.Equal(2, LineHunker.Hunks(before, after).Count);
	}

	[Fact]
	public void LargeFileWithScatteredEdits_KeepsEachEditItsOwnHunk() {
		string[] before = [.. Enumerable.Range(0, 300_000).Select(i => $"line {i}")];
		string[] after = (string[])before.Clone();
		after[5] = "top";
		after[150_000] = "middle";
		after[^5] = "bottom";

		Assert.Equal(3, LineHunker.Hunks(before, after).Count);
	}

	[Fact]
	public void LargeReorderedGeneratedFile_AlignsQuickly() {
		static string[] Type(int i) => [$"type T{i} {{", "  __typename: String", "  id: ID", "}", ""];
		string[] before = [.. Enumerable.Range(0, 60_000).SelectMany(Type)];
		string[] after = [.. Enumerable.Range(0, 60_000).Select(i => (i * 7_919) % 60_000).SelectMany(Type)];

		var ops = LineHunker.Align(before, after);

		Assert.Equal(after, ops.Where(op => op.Kind != LineHunker.LineOpKind.Delete).Select(op => op.Text));
	}

	[Fact]
	public void LargeFileWithoutUniqueLines_AlignsQuickly() {
		string[] before = [.. Enumerable.Range(0, 60_000).SelectMany(_ => new[] { "type {", "  a: A", "  b: B", "}", "" })];
		string[] after = [.. Enumerable.Range(0, 60_000).SelectMany(_ => new[] { "type {", "  b: B", "  a: A", "}", "" })];

		var ops = LineHunker.Align(before, after);

		Assert.Equal(after, ops.Where(op => op.Kind != LineHunker.LineOpKind.Delete).Select(op => op.Text));
		Assert.Equal(before, ops.Where(op => op.Kind != LineHunker.LineOpKind.Insert).Select(op => op.Text));
	}

	[Fact]
	public void Align_IsAValidAlignment_AndExactWithoutRepeatedLines() {
		var random = new Random(1090);
		for (int run = 0; run < 500; run++) {
			string[] a = [.. Enumerable.Range(0, random.Next(0, 30)).Select(_ => "abcd"[random.Next(4)].ToString())];
			string[] b = [.. Enumerable.Range(0, random.Next(0, 30)).Select(_ => "abcde"[random.Next(5)].ToString())];
			AssertValid(a, b);
			string[] distinctA = [.. a.Distinct()];
			string[] distinctB = [.. b.Distinct()];
			Assert.Equal(LcsLength(distinctA, distinctB), AssertValid(distinctA, distinctB));
		}
	}

	private static int AssertValid(string[] a, string[] b) {
		var ops = LineHunker.Align(a, b);
		Assert.Equal(a, ops.Where(op => op.Kind != LineHunker.LineOpKind.Insert).Select(op => op.Text));
		Assert.Equal(b, ops.Where(op => op.Kind != LineHunker.LineOpKind.Delete).Select(op => op.Text));
		return ops.Count(op => op.Kind == LineHunker.LineOpKind.Equal);
	}

	private static int LcsLength(string[] a, string[] b) {
		int[,] lengths = new int[a.Length + 1, b.Length + 1];
		for (int i = 1; i <= a.Length; i++) {
			for (int j = 1; j <= b.Length; j++) {
				lengths[i, j] = a[i - 1] == b[j - 1] ? lengths[i - 1, j - 1] + 1 : Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
			}
		}
		return lengths[a.Length, b.Length];
	}
}
