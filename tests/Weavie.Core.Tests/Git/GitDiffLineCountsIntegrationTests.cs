using System.Diagnostics;
using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Proves the status-bar totals follow Git's diff against HEAD exactly.</summary>
public sealed class GitDiffLineCountsIntegrationTests : IDisposable {
	private readonly string _repo;

	public GitDiffLineCountsIntegrationTests() {
		_repo = Path.Combine(Path.GetTempPath(), "weavie-git-counts-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_repo);
		RunGit("init", "--quiet", "-b", "main");
		File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "original\n");
		RunGit("add", "-A");
		Commit("initial");
	}

	[Fact]
	public async Task HeadDiffCounts_IncludeStagedChangesExcludeUntrackedAndClearOnCommit() {
		var git = new GitService();
		File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "replacement\nsecond\n");
		File.WriteAllText(Path.Combine(_repo, "untracked.txt"), "not in git diff\n");

		Assert.Equal(new GitDiffLineCounts(2, 1), await git.GetHeadDiffLineCountsAsync(_repo));

		RunGit("add", "tracked.txt");
		Assert.Equal(new GitDiffLineCounts(2, 1), await git.GetHeadDiffLineCountsAsync(_repo));

		Commit("update");
		Assert.Equal(new GitDiffLineCounts(0, 0), await git.GetHeadDiffLineCountsAsync(_repo));
		Assert.True(await git.HasUncommittedChangesAsync(_repo));
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
