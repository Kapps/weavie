using System.Text.Json.Nodes;

namespace Weavie.Core.Review;

// Review writes. Each mutation returns nothing useful to Weavie: the caller reloads the review after every write.
public sealed partial class GitHubReviewProvider {
	/// <inheritdoc/>
	public async Task<string> CreateReviewAsync(
		RepoRef repo,
		string pullRequestId,
		string commitSha,
		ReviewVerdict? verdict,
		string body,
		IReadOnlyList<NewReviewComment> threads,
		CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(threads);
		var input = new JsonObject {
			["pullRequestId"] = pullRequestId,
			["commitOID"] = commitSha,
			["body"] = body,
			["threads"] = new JsonArray([.. threads.Select(t => (JsonNode)Thread(t))]),
		};
		// Without an event GitHub leaves the review pending.
		if (verdict is { } submitted) {
			input["event"] = Event(submitted);
		}

		var data = await MutateAsync(repo, "addPullRequestReview", "AddPullRequestReviewInput", "pullRequestReview{id}", input, ct)
			.ConfigureAwait(false);
		return String(data.GetProperty("pullRequestReview"), "id");
	}

	/// <inheritdoc/>
	public Task AddThreadAsync(RepoRef repo, string reviewId, NewReviewComment draft, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(draft);
		var input = Thread(draft);
		input["pullRequestReviewId"] = reviewId;
		return MutateAsync(repo, "addPullRequestReviewThread", "AddPullRequestReviewThreadInput", "thread{id}", input, ct);
	}

	/// <inheritdoc/>
	public Task ReplyAsync(RepoRef repo, string threadId, string? reviewId, string body, CancellationToken ct = default) =>
		MutateAsync(repo, "addPullRequestReviewThreadReply", "AddPullRequestReviewThreadReplyInput", "comment{id}", new JsonObject {
			["pullRequestReviewThreadId"] = threadId,
			["pullRequestReviewId"] = reviewId,
			["body"] = body,
		}, ct);

	/// <inheritdoc/>
	public Task EditAsync(RepoRef repo, string commentId, string body, CancellationToken ct = default) =>
		MutateAsync(repo, "updatePullRequestReviewComment", "UpdatePullRequestReviewCommentInput", "pullRequestReviewComment{id}", new JsonObject {
			["pullRequestReviewCommentId"] = commentId,
			["body"] = body,
		}, ct);

	/// <inheritdoc/>
	public Task DeleteCommentAsync(RepoRef repo, string commentId, CancellationToken ct = default) =>
		MutateAsync(repo, "deletePullRequestReviewComment", "DeletePullRequestReviewCommentInput", "clientMutationId", new JsonObject {
			["id"] = commentId,
		}, ct);

	/// <inheritdoc/>
	public Task SubmitReviewAsync(RepoRef repo, string reviewId, ReviewVerdict verdict, string body, CancellationToken ct = default) =>
		MutateAsync(repo, "submitPullRequestReview", "SubmitPullRequestReviewInput", "pullRequestReview{id}", new JsonObject {
			["pullRequestReviewId"] = reviewId,
			["event"] = Event(verdict),
			["body"] = body,
		}, ct);

	/// <inheritdoc/>
	public Task DiscardReviewAsync(RepoRef repo, string reviewId, CancellationToken ct = default) =>
		MutateAsync(repo, "deletePullRequestReview", "DeletePullRequestReviewInput", "clientMutationId", new JsonObject {
			["pullRequestReviewId"] = reviewId,
		}, ct);

	private async Task<System.Text.Json.JsonElement> MutateAsync(
		RepoRef repo,
		string mutation,
		string inputType,
		string selection,
		JsonObject input,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(repo);
		var data = await GraphQlAsync(
			repo, $"mutation($input:{inputType}!){{{mutation}(input:$input){{{selection}}}}}", new JsonObject { ["input"] = input }, ct)
			.ConfigureAwait(false);
		return data.GetProperty(mutation);
	}

	// Weavie only comments on the head side (new comments on removed lines are out of scope).
	private static JsonObject Thread(NewReviewComment draft) => new() {
		["path"] = draft.Path,
		["line"] = draft.Line,
		["side"] = "RIGHT",
		["body"] = draft.Body,
	};

	private static string Event(ReviewVerdict verdict) => verdict switch {
		ReviewVerdict.Approve => "APPROVE",
		ReviewVerdict.RequestChanges => "REQUEST_CHANGES",
		_ => "COMMENT",
	};
}
