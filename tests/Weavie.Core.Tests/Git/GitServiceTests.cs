using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the <see cref="GitService"/> <c>git worktree list --porcelain</c> parser.</summary>
public sealed class GitServiceTests {
	[Fact]
	public void ParsePorcelain_ParsesBranchesBareAndDetached() {
		string sample =
			"worktree /repo/main\nHEAD aaaa\nbranch refs/heads/main\n\n"
			+ "worktree /repo/feature\nHEAD bbbb\nbranch refs/heads/feature\n\n"
			+ "worktree /repo/bare\nbare\n\n"
			+ "worktree /repo/detached\nHEAD cccc\ndetached\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Equal(4, list.Count);
		Assert.Equal("/repo/main", list[0].Path);
		Assert.Equal("main", list[0].Branch);
		Assert.Equal("aaaa", list[0].Head);
		Assert.Equal("feature", list[1].Branch);
		Assert.True(list[2].IsBare);
		Assert.Null(list[2].Branch);
		Assert.True(list[3].IsDetached);
		Assert.Null(list[3].Branch);
	}

	[Fact]
	public void ParsePorcelain_Empty_ReturnsEmpty() =>
		Assert.Empty(GitService.ParsePorcelainList(string.Empty));

	[Fact]
	public void ParseNumstat_ParsesCountsAndPaths_BinaryAsZero() {
		string sample = "12\t3\tsrc/a.ts\n0\t7\tdocs/b.md\n-\t-\timg/logo.png\n";

		var list = GitService.ParseNumstat(sample);

		Assert.Equal(3, list.Count);
		Assert.Equal("src/a.ts", list[0].Path);
		Assert.Equal(12, list[0].Added);
		Assert.Equal(3, list[0].Removed);
		Assert.Equal(0, list[1].Added);
		Assert.Equal(7, list[1].Removed);
		// Binary files report "-" for both counts → 0/0.
		Assert.Equal("img/logo.png", list[2].Path);
		Assert.Equal(0, list[2].Added);
		Assert.Equal(0, list[2].Removed);
	}

	[Fact]
	public void ParsePorcelain_HandlesLockedAndPrunable() {
		string sample = "worktree /repo/wt\nHEAD dddd\nbranch refs/heads/x\nlocked\nprunable gone\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Single(list);
		Assert.True(list[0].IsLocked);
		Assert.True(list[0].IsPrunable);
		Assert.Equal("x", list[0].Branch);
	}

	[Fact]
	public void ParsePorcelain_NewWorktreeKey_FlushesPreviousBlockWithoutBlankSeparator() {
		// A "worktree" line starts a fresh block even when no blank line separated it from the prior one.
		string sample = "worktree /repo/a\nbranch refs/heads/a\nworktree /repo/b\nbranch refs/heads/b\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Equal(2, list.Count);
		Assert.Equal("/repo/a", list[0].Path);
		Assert.Equal("a", list[0].Branch);
		Assert.Equal("/repo/b", list[1].Path);
		Assert.Equal("b", list[1].Branch);
	}

	[Fact]
	public void ParsePorcelain_ToleratesCrLfAndTrailingBlankLines() {
		string sample = "worktree /repo/main\r\nHEAD aaaa\r\nbranch refs/heads/main\r\n\r\n\r\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Single(list);
		Assert.Equal("main", list[0].Branch);
	}

	[Fact]
	public async Task CommandInMissingWorkingDirectory_ReportsThatDirectory() {
		string missing = Path.Combine(Path.GetTempPath(), "weavie-git-missing-" + Guid.NewGuid().ToString("n"));
		var ex = await Assert.ThrowsAsync<GitException>(() =>
			new GitService().GetCurrentBranchAsync(missing));

		Assert.Contains("working directory does not exist", ex.Message, StringComparison.Ordinal);
		Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ParseGrep_ParsesPathLinePreview() {
		string sample = "src/a.ts:12:const x = 1;\nsrc/a.ts:30:import y;\ndocs/b.md:3:see foo\n";

		var result = GitService.ParseGrep(sample, 500);

		Assert.False(result.Truncated);
		Assert.Equal(3, result.Matches.Count);
		Assert.Equal("src/a.ts", result.Matches[0].Path);
		Assert.Equal(12, result.Matches[0].Line);
		Assert.Equal("const x = 1;", result.Matches[0].Preview);
		Assert.Equal("docs/b.md", result.Matches[2].Path);
	}

	[Fact]
	public void ParseGrep_PreservesColonsInPreview() {
		// The preview itself can hold ':' — only the first two fields are split.
		var result = GitService.ParseGrep("src/a.ts:1:const url = \"http://x:8080\";\n", 500);

		Assert.Single(result.Matches);
		Assert.Equal("const url = \"http://x:8080\";", result.Matches[0].Preview);
	}

	[Fact]
	public void ParseGrep_CapsAndFlagsTruncation() {
		string sample = "a:1:x\na:2:y\na:3:z\n";

		var result = GitService.ParseGrep(sample, 2);

		Assert.True(result.Truncated);
		Assert.Equal(2, result.Matches.Count);
	}

	[Fact]
	public void ParseGrep_SkipsMalformedLines_ToleratesCrLf() {
		string sample = "good:1:hit\r\nnot-a-match-line\r\nbad:notnum:x\r\n";

		var result = GitService.ParseGrep(sample, 500);

		Assert.Single(result.Matches);
		Assert.Equal("good", result.Matches[0].Path);
		Assert.Equal("hit", result.Matches[0].Preview);
	}

	[Fact]
	public void ParseGrep_Empty_ReturnsNoMatches() {
		var result = GitService.ParseGrep(string.Empty, 500);

		Assert.Empty(result.Matches);
		Assert.False(result.Truncated);
	}

	[Theory]
	[InlineData("feature")]
	[InlineData("feature/login")]
	[InlineData("fix-123")]
	[InlineData("user/my.branch")]
	public void IsValidBranchName_AcceptsOrdinaryNames(string name) =>
		Assert.True(GitService.IsValidBranchName(name));

	[Theory]
	[InlineData("")]
	[InlineData("-rf")]                 // leading '-' would parse as a git option
	[InlineData("--upload-pack=evil")]
	[InlineData(".hidden")]
	[InlineData("a..b")]
	[InlineData("a//b")]
	[InlineData("with space")]
	[InlineData("with~tilde")]
	[InlineData("with:colon")]
	[InlineData("with\\backslash")]
	[InlineData("ends/")]
	[InlineData("ends.lock")]
	[InlineData("q?mark")]
	[InlineData("@")]
	public void IsValidBranchName_RejectsMalformedOrOptionShapedNames(string name) =>
		Assert.False(GitService.IsValidBranchName(name));
}
