using System.Text.Json.Serialization;

namespace Weavie.Core.Review;

/// <summary>One review conversation: its comments in order, anchored where the first one is.</summary>
/// <param name="Id">The forge's opaque thread id — the reply target.</param>
/// <param name="Path">The repository-relative file path.</param>
/// <param name="Line">The 1-based line on <paramref name="Side"/>; 0 when <paramref name="Outdated"/>.</param>
/// <param name="Side"><c>"right"</c> (head) or <c>"left"</c> (merge-base).</param>
/// <param name="Outdated">True when the anchor no longer exists in the PR's current diff.</param>
/// <param name="Comments">The opening comment, then its replies in creation order.</param>
public sealed record ReviewThread(
	string Id,
	string Path,
	int Line,
	string Side,
	bool Outdated,
	IReadOnlyList<ReviewComment> Comments) {
	/// <inheritdoc/>
	public bool Equals(ReviewThread? other) =>
		other is not null && Id == other.Id && Path == other.Path && Line == other.Line && Side == other.Side
			&& Outdated == other.Outdated && Comments.SequenceEqual(other.Comments);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Id, Path, Line, Comments.Count);
}

/// <summary>The viewer's unsubmitted review: drafts only they can see until they submit it.</summary>
/// <param name="Id">The forge's opaque review id.</param>
/// <param name="Body">The review summary drafted so far (a review started on the forge's site may carry one).</param>
public sealed record PendingReview(string Id, string Body);

/// <summary>A PR's review state as the viewer sees it: every thread (drafts included) and their pending review.</summary>
/// <param name="PullRequestId">The forge's opaque PR id that new reviews attach to.</param>
/// <param name="Viewer">The authenticated forge user.</param>
/// <param name="ViewerIsAuthor">True when the viewer opened the PR, so they may only comment on it.</param>
/// <param name="Pending">The viewer's pending review, or null.</param>
/// <param name="Threads">The PR's review threads.</param>
public sealed record PullRequestReview(
	string PullRequestId,
	ForgeUser Viewer,
	bool ViewerIsAuthor,
	PendingReview? Pending,
	IReadOnlyList<ReviewThread> Threads) {
	/// <inheritdoc/>
	public bool Equals(PullRequestReview? other) =>
		other is not null && PullRequestId == other.PullRequestId && Viewer == other.Viewer
			&& ViewerIsAuthor == other.ViewerIsAuthor && Pending == other.Pending && Threads.SequenceEqual(other.Threads);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(PullRequestId, Pending, Threads.Count);
}

/// <summary>How a submitted review judges the PR.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewVerdict>))]
public enum ReviewVerdict {
	/// <summary>Feedback without a verdict.</summary>
	[JsonStringEnumMemberName("comment")]
	Comment,

	/// <summary>Ready to merge.</summary>
	[JsonStringEnumMemberName("approve")]
	Approve,

	/// <summary>Must be addressed before merging.</summary>
	[JsonStringEnumMemberName("requestChanges")]
	RequestChanges,
}
