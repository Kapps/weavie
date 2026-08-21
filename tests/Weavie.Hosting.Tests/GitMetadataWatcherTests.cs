using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class GitMetadataWatcherTests {
	[Fact]
	public async Task LinkedWorktreeCommitAndHeadChangeInvalidateStatus() {
		string root = Path.Combine(Path.GetTempPath(), $"weavie-git-watch-{Guid.NewGuid():N}");
		string repository = Path.Combine(root, "repository");
		string worktree = Path.Combine(root, "linked");
		Directory.CreateDirectory(repository);
		try {
			TestHost.RunGit(repository, "init", "--quiet", "--initial-branch=main");
			File.WriteAllText(Path.Combine(repository, "file.txt"), "initial\n");
			TestHost.RunGit(repository, "add", "file.txt");
			Commit(repository, "initial");
			TestHost.RunGit(repository, "branch", "linked");
			TestHost.RunGit(repository, "worktree", "add", "--quiet", worktree, "linked");

			await using var background = new SessionTaskScope(_ => { });
			int invalidations = 0;
			_ = new GitMetadataWatcher(
				background,
				worktree,
				() => Interlocked.Increment(ref invalidations),
				error => throw error);

			File.WriteAllText(Path.Combine(worktree, "file.txt"), "committed\n");
			TestHost.RunGit(worktree, "add", "file.txt");
			Commit(worktree, "linked commit");
			await Wait.UntilAsync(() => Volatile.Read(ref invalidations) > 0);
			int afterCommit = Volatile.Read(ref invalidations);

			TestHost.RunGit(worktree, "switch", "--quiet", "--detach", "HEAD");
			await Wait.UntilAsync(() => Volatile.Read(ref invalidations) > afterCommit);
		} finally {
			if (Directory.Exists(root)) {
				Directory.Delete(root, recursive: true);
			}
		}
	}

	private static void Commit(string repository, string message) => TestHost.RunGit(
		repository,
		"-c", "user.email=test@weavie.dev",
		"-c", "user.name=Weavie Test",
		"-c", "commit.gpgsign=false",
		"commit", "--quiet", "-m", message);
}
