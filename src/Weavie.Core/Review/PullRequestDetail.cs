using System.Text.Json.Serialization;

namespace Weavie.Core.Review;

/// <summary>
/// Reads a PR's overview (description, reviewers, PR-level conversation, checks) and posts PR-level comments,
/// behind a forge-neutral interface beside <see cref="IReviewCommentStore"/>, which owns line threads and reviews.
/// </summary>
public interface IPullRequestDiscussion {
	/// <summary>PR <paramref name="number"/>'s overview, complete: every comment, review, reviewer, and check.</summary>
	Task<PullRequestDetail> LoadDetailAsync(RepoRef repo, int number, CancellationToken ct = default);

	/// <summary>Posts a PR-level comment on PR <paramref name="pullRequestId"/>, immediately and outside any review.</summary>
	Task AddCommentAsync(RepoRef repo, string pullRequestId, string body, CancellationToken ct = default);
}

/// <summary>One PR's overview as its forge reports it.</summary>
/// <param name="Id">The forge's opaque PR id, which PR-level comments are posted to.</param>
/// <param name="Number">The PR number.</param>
/// <param name="Url">The PR's web URL.</param>
/// <param name="BaseRef">The branch the PR merges into.</param>
/// <param name="HeadRef">The branch the PR merges from.</param>
/// <param name="Title">The PR title.</param>
/// <param name="Body">The description (markdown; sanitized before render).</param>
/// <param name="IsDraft">True for a draft PR.</param>
/// <param name="Author">The PR's author.</param>
/// <param name="CreatedAt">ISO-8601 creation timestamp.</param>
/// <param name="Reviewers">Each reviewer's latest standing, requested reviewers included.</param>
/// <param name="Conversation">PR-level comments and submitted reviews, oldest first.</param>
/// <param name="Checks">The head commit's checks and statuses.</param>
public sealed record PullRequestDetail(
	string Id,
	int Number,
	string Url,
	string BaseRef,
	string HeadRef,
	string Title,
	string Body,
	bool IsDraft,
	ForgeUser Author,
	string CreatedAt,
	IReadOnlyList<PullRequestReviewer> Reviewers,
	IReadOnlyList<PullRequestConversationItem> Conversation,
	IReadOnlyList<PullRequestCheck> Checks) {
	/// <inheritdoc/>
	public bool Equals(PullRequestDetail? other) =>
		other is not null && (Id, Number, Url, BaseRef, HeadRef, Title, Body, IsDraft, Author, CreatedAt)
			== (other.Id, other.Number, other.Url, other.BaseRef, other.HeadRef, other.Title, other.Body, other.IsDraft, other.Author, other.CreatedAt)
		&& Reviewers.SequenceEqual(other.Reviewers)
		&& Conversation.SequenceEqual(other.Conversation)
		&& Checks.SequenceEqual(other.Checks);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Id, Number, Title, Conversation.Count, Checks.Count);
}

/// <summary>A reviewer and where their review stands.</summary>
public sealed record PullRequestReviewer(ForgeUser User, ReviewerState State);

/// <summary>Where a reviewer's review stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewerState>))]
public enum ReviewerState {
	/// <summary>Requested, not yet reviewed.</summary>
	[JsonStringEnumMemberName("pending")]
	Pending,

	/// <summary>Reviewed with comments only.</summary>
	[JsonStringEnumMemberName("commented")]
	Commented,

	/// <summary>Approved.</summary>
	[JsonStringEnumMemberName("approved")]
	Approved,

	/// <summary>Requested changes.</summary>
	[JsonStringEnumMemberName("changesRequested")]
	ChangesRequested,
}

/// <summary>A PR-level comment, or a submitted review shown in the conversation.</summary>
/// <param name="Id">The forge's opaque id.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="Body">Markdown, possibly empty for a review with only line comments.</param>
/// <param name="Review">The review's verdict, or null for a plain comment.</param>
/// <param name="CreatedAt">ISO-8601 timestamp it was posted or submitted.</param>
/// <param name="Url">Its web page.</param>
public sealed record PullRequestConversationItem(
	string Id,
	ForgeUser Author,
	string Body,
	ReviewerState? Review,
	string CreatedAt,
	string Url);

/// <summary>One check run or commit status on the PR head.</summary>
public sealed record PullRequestCheck(string Name, CheckState State, string Url);

/// <summary>A check's outcome.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckState>))]
public enum CheckState {
	/// <summary>Queued or running.</summary>
	[JsonStringEnumMemberName("pending")]
	Pending,

	/// <summary>Passed.</summary>
	[JsonStringEnumMemberName("success")]
	Success,

	/// <summary>Failed, errored, timed out, or was cancelled.</summary>
	[JsonStringEnumMemberName("failure")]
	Failure,

	/// <summary>Finished without passing or failing (neutral, skipped, stale).</summary>
	[JsonStringEnumMemberName("neutral")]
	Neutral,
}

/// <summary>A session's PR overview: the last good detail (null without a PR) and the latest load failure.</summary>
public sealed record PullRequestOverviewSnapshot(PullRequestDetail? Detail, string? Error);
