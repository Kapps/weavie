using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCorePullRequestStatusTests {
	[Theory]
	[InlineData(PullRequestState.Open, "open")]
	[InlineData(PullRequestState.Merged, "merged")]
	[InlineData(PullRequestState.Closed, "closed")]
	public async Task Ready_PushesThePullRequestStateForTheCurrentBranch(
		PullRequestState state,
		string expectedState) {
		var pullRequest = new PullRequestSummary {
			Number = 123,
			Title = "Native PR status",
			Author = "Kapps",
			HeadRef = "main",
			BaseRef = "develop",
			Url = "javascript:alert('untrusted forge response')",
			IsDraft = false,
			State = state,
		};
		await using var host = await TestHost.StartAsync(
			repo => {
				TestHost.RunGit(repo, "remote", "add", "origin", "git@github.com:contributor/weavie.git");
				TestHost.RunGit(repo, "remote", "add", "upstream", "git@github.com:Kapps/weavie.git");
			},
			[pullRequest]);

		var message = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.WorkspaceSession.Address, "git", "pullRequest"));

		Assert.Equal("main", message.GetProperty("branch").GetString());
		Assert.Equal(123, message.GetProperty("pullRequest").GetProperty("number").GetInt32());
		Assert.Equal("https://github.com/Kapps/weavie/pull/123", message.GetProperty("pullRequest").GetProperty("url").GetString());
		Assert.Equal(expectedState, message.GetProperty("pullRequest").GetProperty("state").GetString());
	}

	[Fact]
	public async Task Ready_DoesNotProbeAnUntrustedOriginHost() {
		await using var host = await TestHost.StartAsync(
			repo => TestHost.RunGit(repo, "remote", "add", "origin", "https://attacker.example/acme/demo.git"),
			Array.Empty<PullRequestSummary>());

		var message = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.WorkspaceSession.Address, "git", "pullRequest"));

		Assert.Equal(System.Text.Json.JsonValueKind.Null, message.GetProperty("pullRequest").ValueKind);
		Assert.Contains("doesn't support attacker.example", message.GetProperty("error").GetString());
	}

	[Fact]
	public async Task RequesterSync_QueuesBehindTheInFlightBroadcastLookupWithoutCancellingIt() {
		var provider = new ConcurrentProvider();
		await using var host = await TestHost.StartAsync(
			repo => TestHost.RunGit(repo, "remote", "add", "origin", "git@github.com:Kapps/weavie.git"),
			provider);
		await provider.FirstStarted.Task;

		try {
			host.Bridge.Clear();
			await host.SessionRequestAsync<System.Text.Json.JsonElement>(
				host.WorkspaceSession,
				"lifecycle",
				"sync",
				new { });
			Assert.False(provider.FirstCancelled.Task.IsCompleted);
			provider.ReleaseFirst.SetResult();
			var message = await Wait.ForAsync(() =>
				host.Bridge.LastEvent(host.WorkspaceSession.Address, "git", "pullRequest"));

			Assert.Equal(456, message.GetProperty("pullRequest").GetProperty("number").GetInt32());
		} finally {
			provider.ReleaseFirst.TrySetResult();
		}
	}

	private sealed class ConcurrentProvider : IPullRequestProvider {
		private int _calls;

		public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<PullRequestSummary>>([]);

		public async Task<PullRequestSummary?> FindForBranchAsync(
			RepoRef repo, string headOwner, string branch, CancellationToken ct = default) {
			if (Interlocked.Increment(ref _calls) == 1) {
				FirstStarted.SetResult();
				try {
					await ReleaseFirst.Task.WaitAsync(ct);
				} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
					FirstCancelled.SetResult();
					throw;
				}
			}

			return new PullRequestSummary {
				Number = 456,
				Title = "Replacement",
				Author = "Kapps",
				HeadRef = branch,
				BaseRef = "main",
				Url = "ignored",
				IsDraft = false,
				State = PullRequestState.Open,
			};
		}

		public Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<PullRequestSummary>>([]);

		public Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			Task.FromResult<PullRequestSummary?>(null);

		public Task<PullRequestSummary?> FindForCommitAsync(RepoRef repo, string sha, CancellationToken ct = default) =>
			Task.FromResult<PullRequestSummary?>(null);

		public string CommitUrl(RepoRef repo, string sha) => GitHubReviewProvider.WebCommitUrl(repo, sha);

		public string RefUrlBase(RepoRef repo) => GitHubReviewProvider.WebRefUrlBase(repo);
	}

}
