namespace Weavie.Core.Review;

/// <summary>
/// Lists a repository's open pull requests from its forge, behind an interface so the host flow and the
/// integration harness run against a fake (the seam strategy of <c>IGitService</c> and the stubbed
/// <c>claude</c>). GitHub is one implementation (<see cref="GitHubReviewProvider"/>); another forge brings its
/// own. The diff itself is git, not a forge call — see <c>IGitService</c>.
/// </summary>
public interface IPullRequestProvider {
	/// <summary>The open pull requests for <paramref name="repo"/>, most-recently-updated first.</summary>
	Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default);
}
