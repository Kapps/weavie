using Weavie.Core.Corrections;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// Exercises <see cref="CorrectionDiff"/>: hunk grouping/merging, context padding, header coordinates, and
/// the EOL-normalized "no change" cases.
/// </summary>
public sealed class CorrectionDiffTests {
	[Fact]
	public void Unified_IdenticalTexts_ReturnsEmpty() =>
		Assert.Equal(string.Empty, CorrectionDiff.Unified("a\nb\n", "a\nb\n"));

	[Fact]
	public void Unified_EolOnlyDifference_ReturnsEmpty() =>
		Assert.Equal(string.Empty, CorrectionDiff.Unified("a\r\nb\r\n", "a\nb\n"));

	[Fact]
	public void Unified_SingleLineChange_OneHunkWithContext() {
		string before = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"l{i}"));
		string after = before.Replace("l5", "CHANGED", StringComparison.Ordinal);

		string diff = CorrectionDiff.Unified(before, after);

		Assert.Equal("@@ -2,7 +2,7 @@\n l2\n l3\n l4\n-l5\n+CHANGED\n l6\n l7\n l8", diff);
	}

	[Fact]
	public void Unified_FarApartChanges_EmitTwoHunks() {
		string before = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"l{i}"));
		string after = before
			.Replace("l3", "FIRST", StringComparison.Ordinal)
			.Replace("l27", "SECOND", StringComparison.Ordinal);

		string diff = CorrectionDiff.Unified(before, after);

		Assert.Equal(2, diff.Split("@@ -").Length - 1);
		Assert.Contains("-l3\n+FIRST", diff, StringComparison.Ordinal);
		Assert.Contains("-l27\n+SECOND", diff, StringComparison.Ordinal);
	}

	[Fact]
	public void Unified_NearbyChanges_MergeIntoOneHunk() {
		string before = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"l{i}"));
		string after = before
			.Replace("l5", "A", StringComparison.Ordinal)
			.Replace("l9", "B", StringComparison.Ordinal); // 3 equal lines apart ≤ 2×context → one hunk

		string diff = CorrectionDiff.Unified(before, after);

		Assert.Equal(1, diff.Split("@@ -").Length - 1);
	}

	[Fact]
	public void Unified_InsertionOnly_UsesZeroCountAnchor() {
		string diff = CorrectionDiff.Unified(string.Empty, "new");

		Assert.Equal("@@ -0,0 +1,1 @@\n+new", diff);
	}

	[Fact]
	public void Unified_DeletionToEmpty_UsesZeroCountAnchor() {
		string diff = CorrectionDiff.Unified("gone", string.Empty);

		Assert.Equal("@@ -1,1 +0,0 @@\n-gone", diff);
	}

	[Fact]
	public void Unified_ChangeBlock_ShowsDeletesBeforeInserts() {
		string diff = CorrectionDiff.Unified("keep\nold1\nold2\nkeep2", "keep\nnew1\nnew2\nkeep2");

		Assert.Equal("@@ -1,4 +1,4 @@\n keep\n-old1\n-old2\n+new1\n+new2\n keep2", diff);
	}

	[Fact]
	public void Unified_TrailingNewlineRemoved_RegistersAsChange() =>
		Assert.NotEqual(string.Empty, CorrectionDiff.Unified("a\n", "a"));
}
