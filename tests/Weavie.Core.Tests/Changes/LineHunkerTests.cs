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
}
