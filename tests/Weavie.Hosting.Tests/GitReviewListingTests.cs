using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Starting a git review lists exactly what git says changed, whatever the session's earlier review remembered about a
/// file: an edit undone outside the agent, a rejection, or an edit observed late. See docs/specs/review-state.md.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class GitReviewListingTests {
	private static readonly string Doc = string.Concat(Enumerable.Range(1, 700).Select(i => $"line {i}\n"));

	private async Task<List<string>> ReviewAsync(TestHost host) {
		var session = host.SelectedSession;
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n"); // the one real change
		host.Bridge.Clear();
		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });
		var changes = await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));
		return [.. changes.GetProperty("files").EnumerateArray()
			.Select(f => $"{f.GetProperty("name").GetString()} +{f.GetProperty("added").GetInt32()} -{f.GetProperty("removed").GetInt32()}")];
	}

	private static Task<TestHost> StartWithDocAsync() => TestHost.StartAsync(repo => {
		File.WriteAllText(Path.Combine(repo, "big.md"), Doc);
		TempGitRepo.Run(repo, "add", "-A");
		TempGitRepo.Run(repo, "commit", "-q", "-m", "doc");
	});

	[Fact]
	public async Task EditedAcceptedThenChangedBackOutsideTheAgent() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc + "agent line\n");
		changes.RecordChange(path);
		changes.KeepFile(path);                // the user accepts the turn
		Assert.Empty(changes.TurnChanges());
		File.WriteAllText(path, Doc);           // changed back by a shell command (git checkout, sed, ...)

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task EditedAcceptedThenChangedBackByTheAgent() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc + "agent line\n");
		changes.RecordChange(path);
		changes.KeepFile(path);
		changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc);
		changes.RecordChange(path);
		changes.KeepFile(path);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task DeletedAcceptedThenRestored() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		changes.CaptureBaseline(path);
		File.Delete(path);
		changes.RecordChange(path);
		changes.KeepFile(path);
		File.WriteAllText(path, Doc);           // git checkout -- big.md

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task EditedPendingThenChangedBackOutsideTheAgent() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc + "agent line\n");
		changes.RecordChange(path);
		File.WriteAllText(path, Doc);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task RejectedInTheReviewUi() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var session = host.SelectedSession;
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc + "agent line\n");
		session.Changes.RecordChange(path);
		host.SessionEvent(session, "review", "revertFile", new { path });
		await Wait.UntilAsync(() => File.ReadAllText(path) == Doc);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task ReviewOpenThenFileChangedBackAndReviewReopened() {
		// Review open with a real change; the change is undone outside the agent; clicking the chip again must drop it.
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		File.WriteAllText(path, Doc + "uncommitted\n");
		Assert.Equal(["big.md +1 -0", "readme.txt +1 -0"], (await ReviewAsync(host)).Order().ToList());
		host.SelectedSession.Changes.KeepFile(path);
		File.WriteAllText(path, Doc);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task EditObservedAfterTheAgentAlreadyRemovedTheFile() {
		// An agent that writes by itself (ACP) is observed after the fact: the "before" snapshot is taken mid-rewrite.
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		File.Delete(path);                      // the agent's rewrite is under way (delete + recreate)
		changes.CaptureBaseline(path);
		File.WriteAllText(path, Doc);           // ...and lands on the same content, or is later changed back
		changes.RecordChange(path);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task ReviewClosedByABranchSwitchThenReopenedBackOnTheBranch() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		var changes = host.SelectedSession.Changes;
		changes.CaptureBaseline(path);          // the agent touched the file earlier, leaving it unchanged overall
		changes.RecordChange(path);
		TempGitRepo.Run(host.RepoRoot, "checkout", "-q", "-b", "other");
		File.WriteAllText(path, Doc + "other branch\n");
		TempGitRepo.Run(host.RepoRoot, "commit", "-q", "-am", "other");
		changes.CloseReview();                  // what a branch switch does to the open review
		TempGitRepo.Run(host.RepoRoot, "checkout", "-q", "-");

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}

	[Fact]
	public async Task RealChangeObservedLateWhileTheReviewIsOpen() {
		await using var host = await StartWithDocAsync();
		string path = Path.Combine(host.RepoRoot, "big.md");
		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
		var changes = host.SelectedSession.Changes;
		File.WriteAllText(path, Doc + "agent line\n"); // a self-writing agent's edit lands first
		changes.CaptureBaseline(path);                  // ...and is observed afterwards
		changes.RecordChange(path);

		Assert.Equal(["big.md +1 -0", "readme.txt +1 -0"], (await ReviewAsync(host)).Order().ToList());
	}

	[Fact]
	public async Task ABinaryFile_IsLeftOutOfTheListing() {
		await using var host = await StartWithDocAsync();
		File.WriteAllBytes(Path.Combine(host.RepoRoot, "image.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x00]);

		Assert.Equal(["readme.txt +1 -0"], await ReviewAsync(host));
	}
}
