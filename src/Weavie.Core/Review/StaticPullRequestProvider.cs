namespace Weavie.Core.Review;

/// <summary>
/// An in-memory <see cref="IPullRequestProvider"/> + <see cref="IReviewCommentStore"/> — the deterministic
/// stand-in for the headless integration harness and the capture recording, so a PR journey (list, diff,
/// comment, reply, edit, pending review, submit) never touches the network. Writes land as <c>viewer</c> under
/// GitHub's own pending-review rules, so the UI round-trips exactly as it would against the real forge.
/// </summary>
public sealed partial class StaticPullRequestProvider : IPullRequestProvider, IReviewCommentStore {
	private readonly IReadOnlyList<PullRequestSummary> _pullRequests;
	private readonly ForgeUser _viewer;

	/// <summary>
	/// Creates a provider seeded with <paramref name="pullRequests"/> and each PR's review <paramref name="threads"/>,
	/// authenticated as <paramref name="viewer"/>.
	/// </summary>
	public StaticPullRequestProvider(
		IReadOnlyList<PullRequestSummary> pullRequests,
		IReadOnlyList<(int Number, ReviewThread Thread)> threads,
		ForgeUser viewer) {
		ArgumentNullException.ThrowIfNull(pullRequests);
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentNullException.ThrowIfNull(viewer);
		ArgumentException.ThrowIfNullOrWhiteSpace(viewer.Login);
		_pullRequests = pullRequests;
		_threads = [.. threads];
		_viewer = viewer;
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult<IReadOnlyList<PullRequestSummary>>(
			[.. _pullRequests.Where(p => p.State == PullRequestState.Open)]);
	}

	/// <inheritdoc/>
	public Task<PullRequestSummary?> FindForBranchAsync(RepoRef repo, string headOwner, string branch, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		var matches = _pullRequests.Where(p => p.HeadRef.Equals(branch, StringComparison.Ordinal));
		return Task.FromResult(
			matches.FirstOrDefault(p => p.State == PullRequestState.Open) ?? matches.FirstOrDefault());
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string q = query.Trim();
		var open = _pullRequests.Where(p => p.State == PullRequestState.Open);
		if (q.Length == 0) {
			return Task.FromResult<IReadOnlyList<PullRequestSummary>>([.. open]);
		}

		var matches = open
			.Where(p => $"#{p.Number} {p.Title} {p.Author} {p.HeadRef}".Contains(q, StringComparison.OrdinalIgnoreCase))
			.ToList();
		return Task.FromResult<IReadOnlyList<PullRequestSummary>>(matches);
	}

	/// <inheritdoc/>
	public Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(_pullRequests.FirstOrDefault(p => p.Number == number));
	}

	/// <summary>
	/// Commit sha → pull-request number, seeded by the harness so a blamed line resolves to a PR offline. A sha
	/// with no entry has no pull request, exactly as the forge would report.
	/// </summary>
	public Dictionary<string, int> PullRequestsByCommit { get; } = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public Task<PullRequestSummary?> FindForCommitAsync(RepoRef repo, string sha, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(PullRequestsByCommit.TryGetValue(sha, out int number)
			? _pullRequests.FirstOrDefault(p => p.Number == number)
			: null);
	}

	/// <inheritdoc/>
	public string CommitUrl(RepoRef repo, string sha) => GitHubReviewProvider.WebCommitUrl(repo, sha);

	/// <inheritdoc/>
	public string RefUrlBase(RepoRef repo) => GitHubReviewProvider.WebRefUrlBase(repo);
}
