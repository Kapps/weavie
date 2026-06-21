using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Added/removed line counts from the line-level LCS, ignoring CRLF vs LF.</summary>
public sealed class LineDiffTests {
	[Fact]
	public void Count_AddedLines() {
		var (added, removed) = LineDiff.Count("a\nb", "a\nb\nc\nd");
		Assert.Equal(2, added);
		Assert.Equal(0, removed);
	}

	[Fact]
	public void Count_RemovedLines() {
		var (added, removed) = LineDiff.Count("a\nb\nc", "a");
		Assert.Equal(0, added);
		Assert.Equal(2, removed);
	}

	[Fact]
	public void Count_ModifiedLine_CountsAsAddAndRemove() {
		var (added, removed) = LineDiff.Count("a\nb\nc", "a\nB\nc");
		Assert.Equal(1, added);
		Assert.Equal(1, removed);
	}

	[Fact]
	public void Count_CreatedFromEmpty() {
		var (added, removed) = LineDiff.Count("", "x\ny");
		Assert.Equal(2, added);
		Assert.Equal(0, removed);
	}

	[Fact]
	public void Count_IgnoresLineEndingStyle() {
		var (added, removed) = LineDiff.Count("a\r\nb", "a\nb");
		Assert.Equal(0, added);
		Assert.Equal(0, removed);
	}

	[Fact]
	public void FirstChangedLine_Identical_ReturnsNull() =>
		Assert.Null(LineDiff.FirstChangedLine("a\nb\nc", "a\nb\nc"));

	[Fact]
	public void FirstChangedLine_ModifiedMiddleLine_ReturnsThatLine() =>
		Assert.Equal(2, LineDiff.FirstChangedLine("a\nb\nc", "a\nB\nc"));

	[Fact]
	public void FirstChangedLine_TrailingInsertion_ReturnsFirstAddedLine() =>
		Assert.Equal(3, LineDiff.FirstChangedLine("a\nb", "a\nb\nc\nd"));

	[Fact]
	// Trailing deletion has no differing line in the shared prefix; the target clamps into range.
	public void FirstChangedLine_TrailingDeletion_ClampsIntoAfterRange() =>
		Assert.Equal(2, LineDiff.FirstChangedLine("a\nb\nc\nd", "a\nb"));

	[Fact]
	public void FirstChangedLine_CreatedFromEmpty_ReturnsOne() =>
		Assert.Equal(1, LineDiff.FirstChangedLine("", "hello\nworld"));

	[Fact]
	public void FirstChangedLine_IgnoresLineEndingStyle() =>
		Assert.Null(LineDiff.FirstChangedLine("a\r\nb", "a\nb"));
}
