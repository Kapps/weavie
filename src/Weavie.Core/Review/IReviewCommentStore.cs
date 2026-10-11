namespace Weavie.Core.Review;

/// <summary>
/// Loads a PR's review threads and writes comments and reviews, behind a forge-neutral interface (the "similar
/// interface for comments" beside <see cref="IPullRequestProvider"/>). Pending reviews live on the forge, so drafts
/// survive restarts and match the forge's own site. GitHub is one implementation; the integration harness runs
/// against an in-memory fake. The diff the comments anchor to is git's, not the forge's.
/// </summary>
public interface IReviewCommentStore {
	/// <summary>PR <paramref name="number"/>'s threads, drafts included, and the viewer's pending review.</summary>
	Task<PullRequestReview> LoadAsync(RepoRef repo, int number, CancellationToken ct = default);

	/// <summary>
	/// Creates a review at <paramref name="commitSha"/> holding <paramref name="threads"/>: submitted with
	/// <paramref name="verdict"/>, or left pending for the viewer when it's null. Returns the review's id.
	/// </summary>
	Task<string> CreateReviewAsync(
		RepoRef repo,
		string pullRequestId,
		string commitSha,
		ReviewVerdict? verdict,
		string body,
		IReadOnlyList<NewReviewComment> threads,
		CancellationToken ct = default);

	/// <summary>Adds a new thread to the viewer's pending review <paramref name="reviewId"/>.</summary>
	Task AddThreadAsync(RepoRef repo, string reviewId, NewReviewComment draft, CancellationToken ct = default);

	/// <summary>Replies to thread <paramref name="threadId"/>: into pending review <paramref name="reviewId"/>, or posted now when it's null.</summary>
	Task ReplyAsync(RepoRef repo, string threadId, string? reviewId, string body, CancellationToken ct = default);

	/// <summary>Replaces the body of comment <paramref name="commentId"/>, a draft or a posted comment.</summary>
	Task EditAsync(RepoRef repo, string commentId, string body, CancellationToken ct = default);

	/// <summary>Deletes comment <paramref name="commentId"/> (Weavie only deletes pending drafts).</summary>
	Task DeleteCommentAsync(RepoRef repo, string commentId, CancellationToken ct = default);

	/// <summary>Submits pending review <paramref name="reviewId"/> with <paramref name="verdict"/> and summary <paramref name="body"/>.</summary>
	Task SubmitReviewAsync(RepoRef repo, string reviewId, ReviewVerdict verdict, string body, CancellationToken ct = default);

	/// <summary>Deletes pending review <paramref name="reviewId"/> and every draft in it.</summary>
	Task DiscardReviewAsync(RepoRef repo, string reviewId, CancellationToken ct = default);
}

/// <summary>A forge account: its login and avatar image URL (empty when it has none).</summary>
/// <param name="Login">The account login.</param>
/// <param name="AvatarUrl">The avatar image URL, or empty.</param>
public sealed record ForgeUser(string Login, string AvatarUrl);
