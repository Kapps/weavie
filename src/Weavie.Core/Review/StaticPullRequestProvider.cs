namespace Weavie.Core.Review;

/// <summary>
/// An <see cref="IPullRequestProvider"/> backed by a fixed list — the deterministic stand-in for the headless
/// integration harness and the capture recording, so a PR journey never touches the network (the provider
/// analogue of the stubbed <c>claude</c>).
/// </summary>
public sealed class StaticPullRequestProvider : IPullRequestProvider {
	private readonly IReadOnlyList<PullRequestSummary> _pullRequests;

	/// <summary>Creates a provider that always returns <paramref name="pullRequests"/>.</summary>
	public StaticPullRequestProvider(IReadOnlyList<PullRequestSummary> pullRequests) {
		ArgumentNullException.ThrowIfNull(pullRequests);
		_pullRequests = pullRequests;
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(_pullRequests);
	}
}
