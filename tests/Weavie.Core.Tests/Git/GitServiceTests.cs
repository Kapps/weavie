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
	public void ParsePorcelain_HandlesLockedAndPrunable() {
		string sample = "worktree /repo/wt\nHEAD dddd\nbranch refs/heads/x\nlocked\nprunable gone\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Single(list);
		Assert.True(list[0].IsLocked);
		Assert.True(list[0].IsPrunable);
		Assert.Equal("x", list[0].Branch);
	}

	[Fact]
	public void ParsePorcelain_ToleratesCrLfAndTrailingBlankLines() {
		string sample = "worktree /repo/main\r\nHEAD aaaa\r\nbranch refs/heads/main\r\n\r\n\r\n";

		var list = GitService.ParsePorcelainList(sample);

		Assert.Single(list);
		Assert.Equal("main", list[0].Branch);
	}
}
