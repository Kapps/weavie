using System.Diagnostics;
using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Proves the status-bar totals cover every change the HEAD review surfaces.</summary>
public sealed class GitDiffLineCountsIntegrationTests : IDisposable {
	private readonly string _repo;

	public GitDiffLineCountsIntegrationTests() {
		_repo = Path.Combine(Path.GetTempPath(), "weavie-git-counts-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_repo);
		RunGit("init", "--quiet", "-b", "main");
		File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "original\n");
		File.WriteAllText(Path.Combine(_repo, ".gitignore"), "ignored.txt\n");
		RunGit("add", "-A");
		Commit("initial");
	}

	[Fact]
	public async Task HeadDiffCounts_IncludeTrackedAndUntrackedChangesAndClearWithTheWorktree() {
		var git = new GitService();
		File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "replacement\nsecond\n");
		string untracked = Path.Combine(_repo, "untracked.txt");
		File.WriteAllText(untracked, "new file\n");
		File.WriteAllText(Path.Combine(_repo, "ignored.txt"), "not a worktree change\n");

		Assert.Equal(new GitDiffLineCounts(3, 1), await git.GetHeadDiffLineCountsAsync(_repo));

		RunGit("add", "tracked.txt");
		Assert.Equal(new GitDiffLineCounts(3, 1), await git.GetHeadDiffLineCountsAsync(_repo));

		Commit("update");
		Assert.Equal(new GitDiffLineCounts(1, 0), await git.GetHeadDiffLineCountsAsync(_repo));

		File.Delete(untracked);
		Assert.Equal(new GitDiffLineCounts(0, 0), await git.GetHeadDiffLineCountsAsync(_repo));
		Assert.False(await git.HasUncommittedChangesAsync(_repo));
	}

	private void Commit(string message) =>
		RunGit(
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", message);

	private void RunGit(params string[] args) {
		var info = new ProcessStartInfo("git") {
			WorkingDirectory = _repo,
			UseShellExecute = false,
			RedirectStandardError = true,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info) ?? throw new InvalidOperationException("git failed to start");
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
		}
	}

	public void Dispose() {
		try {
			Directory.Delete(_repo, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Best-effort temp cleanup; a lingering handle may outlive the test briefly on Windows.
		}
	}
}
