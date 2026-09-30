namespace Weavie.Core.Review;

/// <summary>
/// Loads and posts a PR's review comments, behind a forge-neutral interface (the "similar interface for
/// comments" beside <see cref="IPullRequestProvider"/>). GitHub is one implementation; the integration harness
/// runs against an in-memory fake. The diff the comments anchor to is git's, not the forge's.
/// </summary>
public interface IReviewCommentStore {
	/// <summary>Every review comment on PR <paramref name="number"/> (all pages), in creation order.</summary>
	Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default);

	/// <summary>Posts a new top-level review comment on PR <paramref name="number"/> at <paramref name="commitId"/>; returns the created comment.</summary>
	Task<ReviewComment> AddAsync(RepoRef repo, int number, string commitId, NewReviewComment draft, CancellationToken ct = default);

	/// <summary>Replies to comment <paramref name="inReplyTo"/> on PR <paramref name="number"/>; returns the created reply.</summary>
	Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string body, CancellationToken ct = default);

	/// <summary>Replaces the body of review comment <paramref name="id"/>; returns the edited comment.</summary>
	Task<ReviewComment> EditAsync(RepoRef repo, long id, string body, CancellationToken ct = default);

	/// <summary>The login of the user the forge authenticates these calls as.</summary>
	Task<string> ViewerLoginAsync(RepoRef repo, CancellationToken ct = default);
}
