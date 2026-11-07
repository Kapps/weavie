namespace Weavie.Core.Review;

// The overview half of the fake: each PR's seeded detail, with the viewer's PR-level comments, submitted reviews, and
// line comments appended as posted.
public sealed partial class StaticPullRequestProvider : IPullRequestDiscussion {
	/// <summary>Seeded overviews by PR number; a PR without one reads as its summary with no discussion or checks.</summary>
	public Dictionary<int, PullRequestDetail> Details { get; } = [];

	/// <inheritdoc/>
	public Task<PullRequestDetail> LoadDetailAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		lock (_gate) {
			return Task.FromResult(Detail(number) ?? throw new InvalidOperationException($"GitHub has no PR #{number} in {repo.Owner}/{repo.Name}."));
		}
	}

	/// <inheritdoc/>
	public Task AddCommentAsync(RepoRef repo, string pullRequestId, string body, CancellationToken ct = default) {
		lock (_gate) {
			int number = _pullRequests.FirstOrDefault(p => PullRequestId(p.Number) == pullRequestId)?.Number
				?? throw new InvalidOperationException($"Could not resolve to a node with the global id of '{pullRequestId}'.");
			AppendConversation(number, [Entry($"comment-{++_nextId}", body, null, null)]);
			return Task.CompletedTask;
		}
	}

	// Called holding _gate, the review's line comments still pending: submitting publishes them with it.
	private void RecordReview(int number, ReviewVerdict verdict, string body) {
		SubmittedReviews.Add((number, verdict, body));
		var review = Entry($"review-{++_nextId}", body, verdict switch {
			ReviewVerdict.Approve => ReviewerState.Approved,
			ReviewVerdict.RequestChanges => ReviewerState.ChangesRequested,
			ReviewVerdict.Comment => ReviewerState.Commented,
			_ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "unhandled review verdict"),
		}, null);
		var lineComments = _threads.Where(t => t.Number == number)
			.SelectMany(t => t.Thread.Comments.Where(c => c.Pending).Select(c => LineEntry(t.Thread, c)));
		AppendConversation(number, PullRequestConversationItem.ForReview(review, lineComments));
		MapDrafts(number, c => c with { Pending = false });
	}

	private PullRequestConversationItem LineEntry(ReviewThread thread, ReviewComment comment) =>
		Entry(comment.Id, comment.Body, null, new(thread.Path, thread.Outdated ? null : thread.Line));

	private PullRequestConversationItem Entry(string id, string body, ReviewerState? review, PullRequestCommentLocation? location) =>
		new(id, _viewer, body, review, Now(), string.Empty, location);

	private void AppendConversation(int number, IEnumerable<PullRequestConversationItem> items) {
		var detail = Detail(number) ?? throw new InvalidOperationException($"No pull request #{number}.");
		Details[number] = detail with { Conversation = [.. detail.Conversation, .. items] };
	}

	private PullRequestDetail? Detail(int number) =>
		Details.GetValueOrDefault(number) ?? (_pullRequests.FirstOrDefault(p => p.Number == number) is { } pr
			? new PullRequestDetail(PullRequestId(number), number, pr.Url, pr.BaseRef, pr.HeadRef, pr.Title, string.Empty, pr.IsDraft,
				new ForgeUser(pr.Author, string.Empty), string.Empty, [], [], [])
			: null);
}
