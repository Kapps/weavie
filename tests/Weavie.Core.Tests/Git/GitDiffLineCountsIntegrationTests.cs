using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Proves the status-bar totals cover every change the HEAD review surfaces.</summary>
public sealed class GitDiffLineCountsIntegrationTests : IDisposable {
	private readonly TempGitRepo _repo = new("weavie-git-counts");

	public GitDiffLineCountsIntegrationTests() {
		_repo.Write("tracked.txt", "original\n");
		_repo.Write(".gitignore", "ignored.txt\n");
		_repo.Commit("initial");
	}

	[Fact]
	public async Task HeadDiffCounts_IncludeTrackedAndUntrackedChangesAndClearWithTheWorktree() {
		var git = new GitService();
		_repo.Write("tracked.txt", "replacement\nsecond\n");
		string untracked = _repo.Write("untracked.txt", "new file\n");
		_repo.Write("ignored.txt", "not a worktree change\n");

		Assert.Equal(new GitDiffLineCounts(3, 1), await git.GetHeadDiffLineCountsAsync(_repo.Path));

		_repo.Git("add", "tracked.txt");
		Assert.Equal(new GitDiffLineCounts(3, 1), await git.GetHeadDiffLineCountsAsync(_repo.Path));

		// Commit only what is staged: the untracked file must stay a pending worktree change.
		_repo.Git("commit", "--quiet", "-m", "update");
		Assert.Equal(new GitDiffLineCounts(1, 0), await git.GetHeadDiffLineCountsAsync(_repo.Path));

		File.Delete(untracked);
		Assert.Equal(new GitDiffLineCounts(0, 0), await git.GetHeadDiffLineCountsAsync(_repo.Path));
		Assert.False(await git.HasUncommittedChangesAsync(_repo.Path));
	}

	public void Dispose() => _repo.Dispose();
}
