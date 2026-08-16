using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the unified-diff reader behind the blame popover's "what changed here" hunk.</summary>
public sealed class UnifiedDiffTests {
	private const string TwoHunks =
		"diff --git a/notes.md b/notes.md\n"
		+ "index f3a1f8cf..a00766d3 100644\n"
		+ "--- a/notes.md\n"
		+ "+++ b/notes.md\n"
		+ "@@ -2,3 +2,4 @@ section one\n"
		+ " context\n"
		+ "-gone\n"
		+ "+added\n"
		+ "+also added\n"
		+ " tail\n"
		+ "@@ -20,2 +21,2 @@\n"
		+ " keep\n"
		+ "-old\n"
		+ "+new\n";

	[Fact]
	public void HunkContaining_PicksTheHunkCoveringTheLineOnTheNewSide() {
		// The first hunk's post-image covers lines 2..5, the second's 21..22.
		Assert.Equal(2, UnifiedDiff.HunkContaining(TwoHunks, 3)?.NewStart);
		Assert.Equal(2, UnifiedDiff.HunkContaining(TwoHunks, 5)?.NewStart);
		Assert.Equal(21, UnifiedDiff.HunkContaining(TwoHunks, 22)?.NewStart);
		Assert.Null(UnifiedDiff.HunkContaining(TwoHunks, 6));
		Assert.Null(UnifiedDiff.HunkContaining(TwoHunks, 100));
	}

	[Fact]
	public void Hunks_KeepMarkersAndStopAtTheDeclaredCounts() {
		var hunks = UnifiedDiff.Hunks(TwoHunks);

		Assert.Equal(2, hunks.Count);
		Assert.Equal("@@ -2,3 +2,4 @@ section one", hunks[0].Header);
		Assert.Equal(2, hunks[0].OldStart);
		Assert.Equal([" context", "-gone", "+added", "+also added", " tail"], hunks[0].Lines);
		Assert.Equal([" keep", "-old", "+new"], hunks[1].Lines);
	}

	[Fact]
	public void Hunks_ReadFileContentThatLooksLikeADiffHeader() {
		// The counts, not the prefixes, decide where a hunk ends — so a patch of a patch stays one hunk.
		string diff =
			"@@ -1,2 +1,3 @@\n"
			+ " diff --git a/x b/x\n"
			+ "+@@ -9,9 +9,9 @@\n"
			+ " --- a/x\n";

		var hunk = Assert.Single(UnifiedDiff.Hunks(diff));

		Assert.Equal(3, hunk.Lines.Count);
		Assert.Equal(1, UnifiedDiff.HunkContaining(diff, 2)?.NewStart);
	}

	[Fact]
	public void Hunks_CountAnAbsentTrailingNewlineMarkerAgainstNeitherSide() {
		// The marker sits between the two sides when a file gains a trailing newline; it belongs to the hunk but
		// consumes neither budget, so the context line after it is still read.
		string diff = "@@ -1,2 +1,2 @@\n-old\n\\ No newline at end of file\n+old\n keep\n";

		var hunk = Assert.Single(UnifiedDiff.Hunks(diff));

		Assert.Equal(["-old", "\\ No newline at end of file", "+old", " keep"], hunk.Lines);
		Assert.Equal(1, UnifiedDiff.HunkContaining(diff, 2)?.NewStart);
	}

	[Fact]
	public void TryParseNewStart_ReadsOnlyHunkHeaders() {
		Assert.True(UnifiedDiff.TryParseNewStart("@@ -5,0 +10,1 @@", out int newStart));
		Assert.Equal(10, newStart);
		Assert.False(UnifiedDiff.TryParseNewStart("+@@ -5,0 +10,1 @@", out _));
		Assert.False(UnifiedDiff.TryParseNewStart("diff --git a/x b/x", out _));
	}

	[Fact]
	public void Hunks_NoDiff_IsEmpty() => Assert.Empty(UnifiedDiff.Hunks(string.Empty));
}
