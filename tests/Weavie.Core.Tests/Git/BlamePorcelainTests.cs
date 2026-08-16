using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the <c>git blame --porcelain</c> parser.</summary>
public sealed class BlamePorcelainTests {
	private const string ShaA = "bb96708a0183a00d92ea1c7ce608f744c38a4b3b";
	private const string ShaB = "649779a60ae4209a954310595b7eec1355dd1537";

	// Git writes a commit's headers only the first time it appears; every later group of its lines is just the
	// "<sha> <orig> <final>" line and the content. Both forms must land on the same commit.
	private const string Sample =
		$"{ShaA} 1 1 2\n"
		+ "author Kapps\n"
		+ "author-mail <kapps@example.com>\n"
		+ "author-time 1783752905\n"
		+ "author-tz +0000\n"
		+ "committer Kapps\n"
		+ "summary Share agent guidance\n"
		+ "filename AGENTS.md\n"
		+ "\t# Weavie\n"
		+ $"{ShaA} 2 2\n"
		+ "\t\n"
		+ $"{ShaB} 7 3 1\n"
		+ "author Other\n"
		+ "author-mail <other@example.com>\n"
		+ "author-time 1785820887\n"
		+ "summary Preserve transcripts\n"
		+ "previous " + ShaA + " AGENTS.md\n"
		+ "filename AGENTS.md\n"
		+ "\tthird line\n";

	[Fact]
	public void Parse_DeduplicatesCommitsAndMapsEveryLine() {
		var blame = BlamePorcelain.Parse(Sample);

		Assert.Equal(2, blame.Commits.Count);
		Assert.Equal(new[] { 0, 0, 1 }, blame.LineCommits);
		// The third line is line 7 in the commit that wrote it — the anchor for its hunk.
		Assert.Equal(new[] { 1, 2, 7 }, blame.LineOriginalLines);

		var first = blame.Commits[0];
		Assert.Equal(ShaA, first.Sha);
		Assert.Equal("Kapps", first.Author);
		Assert.Equal("kapps@example.com", first.AuthorEmail);
		Assert.Equal(1783752905, first.TimeUnix);
		Assert.Equal("Share agent guidance", first.Summary);
		Assert.False(first.Uncommitted);
		Assert.Equal("Preserve transcripts", blame.Commits[1].Summary);
	}

	[Fact]
	public void Parse_MarksTheAllZeroShaAsUncommitted() {
		string sample =
			$"{BlamePorcelain.UncommittedSha} 4 1 1\n"
			+ "author Not Committed Yet\n"
			+ "author-mail <not.committed.yet>\n"
			+ "author-time 1786857122\n"
			+ "summary Version of AGENTS.md from AGENTS.md\n"
			+ "filename AGENTS.md\n"
			+ "\tjust typed\n";

		var blame = BlamePorcelain.Parse(sample);

		Assert.True(Assert.Single(blame.Commits).Uncommitted);
	}

	[Fact]
	public void Parse_HandlesCrLfAndAContentLineThatLooksLikeAHeader() {
		// The blamed line's own text starts with something shaped exactly like an entry header; the leading tab
		// is what distinguishes content, so it must not be read as a new group.
		string sample =
			$"{ShaA} 1 1 1\r\n"
			+ "author Kapps\r\n"
			+ "author-time 1783752905\r\n"
			+ "summary Add a sample\r\n"
			+ "filename notes.txt\r\n"
			+ $"\t{ShaB} 9 9 9\r\n";

		var blame = BlamePorcelain.Parse(sample);

		Assert.Equal(ShaA, Assert.Single(blame.Commits).Sha);
		Assert.Equal(new[] { 0 }, blame.LineCommits);
	}

	[Fact]
	public void Parse_EmptyOutput_IsEmpty() => Assert.Same(GitBlame.Empty, BlamePorcelain.Parse(string.Empty));
}
