using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// An open review belongs to the branch it was opened on: checking out another branch closes it, and merging its
/// target in moves its merge-base so the target's merged work never shows as the branch's own changes.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class ReviewFollowsHeadTests {
	[Fact]
	public async Task SwitchingBranch_ClosesTheReview_SoReviewingAgainShowsOnlyTheNewBranch() {
		await using var host = await TestHost.StartAsync(repo => {
			Branch(repo, "topic", "topic.txt");
			TempGitRepo.Run(repo, "checkout", "-q", "main");
			Commit(repo, "main.txt", "main moved on");
			TempGitRepo.Run(repo, "checkout", "-q", "topic");
		});
		var session = host.SelectedSession;
		var opened = await host.SessionRequestAsync<JsonElement>(
			session, "review", "diffAgainst", new { reference = "main", replaces = (string?)null });
		Assert.True(opened.GetProperty("opened").GetBoolean());

		TempGitRepo.Run(host.RepoRoot, "switch", "-q", "-c", "fresh", "main");

		await Wait.UntilAsync(() => host.Bridge.PostedEvents("notifications", "show").Any(toast =>
			toast.GetProperty("message").GetString()!.Contains("now on 'fresh'", StringComparison.Ordinal)));
		var reopened = await host.SessionRequestAsync<JsonElement>(
			session, "review", "diffAgainst", new { reference = "main", replaces = (string?)null });
		Assert.False(reopened.GetProperty("opened").GetBoolean());
		Assert.Contains(
			host.Bridge.PostedEvents("notifications", "show"),
			toast => toast.GetProperty("message").GetString() == "No changes against 'main'.");
	}

	[Fact]
	public async Task MergingTheTarget_MovesTheReviewsMergeBase_SoItsWorkIsNotReviewed() {
		await using var host = await TestHost.StartAsync(repo => {
			Branch(repo, "topic", "topic.txt");
			TempGitRepo.Run(repo, "checkout", "-q", "main");
			Commit(repo, "main.txt", "main moved on");
			TempGitRepo.Run(repo, "checkout", "-q", "topic");
		});
		var session = host.SelectedSession;
		await host.SessionRequestAsync<JsonElement>(
			session, "review", "diffAgainst", new { reference = "main", replaces = (string?)null });

		TempGitRepo.Run(host.RepoRoot, "merge", "-q", "--no-edit", "main");

		string mergeBase = TempGitRepo.Run(host.RepoRoot, "rev-parse", "main").Trim();
		await Wait.UntilAsync(() => session.Changes.Review?.MergeBase == mergeBase);
		var files = host.Bridge.LastEvent(session.Address, "review", "changes")!.Value.GetProperty("files")
			.EnumerateArray().Select(file => Path.GetFileName(file.GetProperty("path").GetString())).ToList();
		Assert.Equal(["topic.txt"], files);
	}

	[Fact]
	public async Task RebasingOntoTheTarget_KeepsTheReviewOpen_AtItsNewMergeBase() {
		await using var host = await TestHost.StartAsync(repo => {
			Branch(repo, "topic", "topic.txt");
			TempGitRepo.Run(repo, "checkout", "-q", "main");
			Commit(repo, "main.txt", "main moved on");
			TempGitRepo.Run(repo, "checkout", "-q", "topic");
		});
		var session = host.SelectedSession;
		await host.SessionRequestAsync<JsonElement>(
			session, "review", "diffAgainst", new { reference = "main", replaces = (string?)null });

		TempGitRepo.Run(host.RepoRoot, "rebase", "-q", "main");

		string mergeBase = TempGitRepo.Run(host.RepoRoot, "rev-parse", "main").Trim();
		await Wait.UntilAsync(() => session.Changes.Review?.MergeBase == mergeBase);
		Assert.Equal("topic", session.Changes.Review!.Branch);
	}

	private static void Branch(string repo, string branch, string file) {
		TempGitRepo.Run(repo, "checkout", "-q", "-b", branch);
		Commit(repo, file, $"{branch} work");
	}

	private static void Commit(string repo, string file, string message) {
		File.WriteAllText(Path.Combine(repo, file), $"{message}\n");
		TempGitRepo.Run(repo, "add", "-A");
		TempGitRepo.Run(repo, "commit", "-q", "-m", message);
	}
}
