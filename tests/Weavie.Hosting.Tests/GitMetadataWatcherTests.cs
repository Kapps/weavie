using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class GitMetadataWatcherTests {
	[Fact]
	public async Task LinkedWorktreeCommitAndHeadChangeInvalidateStatus() {
		using var root = new TempDirectory("weavie-git-watch");
		string repository = root.CreateDirectory("repository");
		string worktree = root.Combine("linked");
		TempGitRepo.Init(repository);
		File.WriteAllText(Path.Combine(repository, "file.txt"), "initial\n");
		TempGitRepo.Run(repository, "add", "file.txt");
		Commit(repository, "initial");
		TempGitRepo.Run(repository, "branch", "linked");
		TempGitRepo.Run(repository, "worktree", "add", "--quiet", worktree, "linked");

		await using var background = new SessionTaskScope(_ => { });
		int invalidations = 0;
		_ = new GitMetadataWatcher(
			background,
			worktree,
			() => Interlocked.Increment(ref invalidations),
			error => throw error);

		File.WriteAllText(Path.Combine(worktree, "file.txt"), "committed\n");
		TempGitRepo.Run(worktree, "add", "file.txt");
		Commit(worktree, "linked commit");
		await Wait.UntilAsync(() => Volatile.Read(ref invalidations) > 0);
		int afterCommit = Volatile.Read(ref invalidations);

		TempGitRepo.Run(worktree, "switch", "--quiet", "--detach", "HEAD");
		await Wait.UntilAsync(() => Volatile.Read(ref invalidations) > afterCommit);
	}

	private static void Commit(string repository, string message) =>
		TempGitRepo.Run(repository, "commit", "--quiet", "-m", message);
}
