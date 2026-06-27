namespace Weavie.Core.Review;

/// <summary>
/// Lists a repository's open pull requests from its forge, behind an interface so the host flow and the
/// integration harness run against a fake (the seam strategy of <c>IGitService</c> and the stubbed
/// <c>claude</c>). GitHub is one implementation (<see cref="GitHubReviewProvider"/>); another forge brings its
/// own. The diff itself is git, not a forge call — see <c>IGitService</c>.
/// </summary>
public interface IPullRequestProvider {
	/// <summary>The open pull requests for <paramref name="repo"/>, most-recently-updated first — the picker's default list.</summary>
	Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default);

	/// <summary>
	/// Pull requests matching <paramref name="query"/> (forge-side search), so the picker scales past the default
	/// list without fetching everything. Results may omit branch refs — resolve via <see cref="GetAsync"/> on open.
	/// </summary>
	Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default);

	/// <summary>
	/// One pull request by number (any state), or <c>null</c> when it doesn't exist — how a typed <c>#N</c> / a
	/// pasted URL / a picked search result resolves its branch refs at open time.
	/// </summary>
	Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default);
}
