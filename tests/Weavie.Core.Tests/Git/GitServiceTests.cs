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
	public void ParseRecentBranches_SplitsTheConfiguredAuthorsBranchesFromTheRest() {
		string sample = "refs/heads/kapps/fix-webm\t<me@weavie.dev>\n"
			+ "refs/heads/team/inbox\t<other@example.com>\n"
			+ "refs/heads/main\t<ME@weavie.dev>\n";

		var recent = GitService.ParseRecentBranches(sample, new GitIdentity("Me", "me@weavie.dev"), 20);

		Assert.Equal(["kapps/fix-webm", "main"], recent.Mine);
		Assert.Equal(["team/inbox"], recent.Others);
	}

	[Fact]
	public void ParseRecentBranches_DropsTheRemoteFromTrackingRefsAndNamesEachBranchOnce() {
		string sample = "refs/remotes/origin/HEAD\t<other@example.com>\n"
			+ "refs/remotes/origin/kapps/fix-webm\t<me@weavie.dev>\n"
			+ "refs/heads/kapps/fix-webm\t<me@weavie.dev>\n"
			+ "refs/remotes/upstream/team/inbox\t<other@example.com>\n";

		var recent = GitService.ParseRecentBranches(sample, new GitIdentity("Me", "me@weavie.dev"), 20);

		Assert.Equal(["kapps/fix-webm"], recent.Mine);
		Assert.Equal(["team/inbox"], recent.Others);
	}

	[Fact]
	public void ParseRecentBranches_UnsetIdentityOwnsNothingAndTheLimitAppliesPerGroup() {
		string sample = "refs/heads/a\t<me@weavie.dev>\nrefs/heads/b\t<me@weavie.dev>\nrefs/heads/c\t<other@example.com>\n";

		Assert.Empty(GitService.ParseRecentBranches(sample, new GitIdentity("", ""), 20).Mine);
		Assert.Equal(["a"], GitService.ParseRecentBranches(sample, new GitIdentity("Me", "me@weavie.dev"), 1).Mine);
	}

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
	public void ParseStatusSummary_ReturnsBranchAndDirtyState() {
		string sample = "# branch.oid abc123\n# branch.head feature/counts\n1 .M N... 100644 100644 100644 abc123 abc123 tracked.txt\n";

		Assert.Equal(new GitStatusSummary("feature/counts", true), GitService.ParseStatusSummary(sample));
	}

	[Fact]
	public void ParseStatusSummary_DetachedCleanHead() {
		string sample = "# branch.oid abc123\n# branch.head (detached)\n";

		Assert.Equal(new GitStatusSummary(null, false), GitService.ParseStatusSummary(sample));
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
	public async Task ListWorkspaceFiles_NonRepositoryReturnsNull() {
		string directory = Path.Combine(Path.GetTempPath(), "weavie-non-repo-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(directory);
		try {
			Assert.Null(await new GitService().ListWorkspaceFilesAsync(directory));
		} finally {
			Directory.Delete(directory);
		}
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

	[Theory]
	[InlineData("HEAD")]
	[InlineData("foo/.bar")]
	[InlineData("foo.lock/bar")]
	[InlineData("foo/bar.lock")]
	public async Task IsValidBranchNameAsync_RejectsNamesReservedByGit(string name) =>
		Assert.False(await new GitService().IsValidBranchNameAsync(Directory.GetCurrentDirectory(), name));
}
