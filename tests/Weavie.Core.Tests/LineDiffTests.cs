using Weavie.Core.Changes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Added/removed line counts via the LCS of lines, line-ending-insensitive.</summary>
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
}
