using Weavie.Core.Review;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCorePullRequestStatusTests {
	[Fact]
	public async Task Ready_PushesTheOpenPullRequestForTheCurrentBranch() {
		var pullRequest = new PullRequestSummary {
			Number = 123,
			Title = "Native PR status",
			Author = "Kapps",
			HeadRef = "main",
			BaseRef = "develop",
			Url = "javascript:alert('untrusted forge response')",
			IsDraft = false,
		};
		await using var host = await TestHost.StartAsync(
			repo => {
				TestHost.RunGit(repo, "remote", "add", "origin", "git@github.com:contributor/weavie.git");
				TestHost.RunGit(repo, "remote", "add", "upstream", "git@github.com:Kapps/weavie.git");
			},
			[pullRequest]);

		var message = await Wait.ForAsync(() => host.Bridge.LastOfType("pull-request-status"));

		Assert.Equal("main", message.GetProperty("branch").GetString());
		Assert.Equal(host.PrimaryId, message.GetProperty("slot").GetString());
		Assert.Equal(123, message.GetProperty("pullRequest").GetProperty("number").GetInt32());
		Assert.Equal("https://github.com/Kapps/weavie/pull/123", message.GetProperty("pullRequest").GetProperty("url").GetString());
	}

	[Fact]
	public async Task Ready_DoesNotProbeAnUntrustedOriginHost() {
		await using var host = await TestHost.StartAsync(
			repo => TestHost.RunGit(repo, "remote", "add", "origin", "https://attacker.example/acme/demo.git"),
			Array.Empty<PullRequestSummary>());

		var message = await Wait.ForAsync(() => host.Bridge.LastOfType("pull-request-status"));

		Assert.Equal(System.Text.Json.JsonValueKind.Null, message.GetProperty("pullRequest").ValueKind);
		Assert.Contains("doesn't support attacker.example", message.GetProperty("error").GetString());
	}

	[Fact]
	public async Task NewRefresh_CancelsAndReplacesTheInFlightLookup() {
		var provider = new SupersededProvider();
		await using var host = await TestHost.StartAsync(
			repo => TestHost.RunGit(repo, "remote", "add", "origin", "git@github.com:Kapps/weavie.git"),
			provider);
		await provider.FirstStarted.Task;

		host.Send("""{"type":"ready"}""");
		var message = await Wait.ForAsync(() => host.Bridge.LastOfType("pull-request-status"));

		await provider.FirstCancelled.Task;
		Assert.Equal(456, message.GetProperty("pullRequest").GetProperty("number").GetInt32());
	}

	private sealed class SupersededProvider : IPullRequestProvider {
		private int _calls;

		public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FirstCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<PullRequestSummary>>([]);

		public async Task<PullRequestSummary?> FindOpenForBranchAsync(
			RepoRef repo, string headOwner, string branch, CancellationToken ct = default) {
			if (Interlocked.Increment(ref _calls) == 1) {
				FirstStarted.SetResult();
				try {
					await Task.Delay(Timeout.InfiniteTimeSpan, ct);
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
			};
		}

		public Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<PullRequestSummary>>([]);

		public Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default) =>
			Task.FromResult<PullRequestSummary?>(null);

		public string RefUrlBase(RepoRef repo) => GitHubReviewProvider.WebRefUrlBase(repo);
	}
}
