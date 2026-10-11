using System.Globalization;

namespace Weavie.Core.Review;

// The review half of the fake, enforcing the rules GitHub enforces: one pending review per viewer per PR, replies
// joining it while it exists, and no verdict on the viewer's own PR. Every draft belongs to the viewer.
public sealed partial class StaticPullRequestProvider {
	private readonly Lock _gate = new();
	private readonly List<(int Number, ReviewThread Thread)> _threads;
	private long _nextId;

	/// <summary>The viewer's pending review per PR number; the harness seeds one "started on the forge's site".</summary>
	public Dictionary<int, PendingReview> PendingReviews { get; } = [];

	/// <summary>Every review submitted so far, in order, for tests to assert on.</summary>
	public List<(int Number, ReviewVerdict Verdict, string Body)> SubmittedReviews { get; } = [];

	/// <inheritdoc/>
	public Task<PullRequestReview> LoadAsync(RepoRef repo, int number, CancellationToken ct = default) {
		lock (_gate) {
			return Task.FromResult(new PullRequestReview(
				PullRequestId(number),
				_viewer,
				ViewerIsAuthor(number),
				PendingReviews.GetValueOrDefault(number),
				[.. _threads.Where(t => t.Number == number).Select(t => t.Thread with {
					Comments = [.. t.Thread.Comments.Select(c => c with { Mine = IsViewer(c.Author) })],
				})]));
		}
	}

	/// <inheritdoc/>
	public Task<string> CreateReviewAsync(
		RepoRef repo,
		string pullRequestId,
		string commitSha,
		ReviewVerdict? verdict,
		string body,
		IReadOnlyList<NewReviewComment> threads,
		CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(threads);
		lock (_gate) {
			int number = _pullRequests.FirstOrDefault(p => PullRequestId(p.Number) == pullRequestId)?.Number
				?? throw new InvalidOperationException($"No pull request {pullRequestId}.");
			if (PendingReviews.ContainsKey(number)) {
				throw new InvalidOperationException("User can only have one pending review per pull request");
			}

			if (verdict is { } submitted) {
				RequireVerdict(number, submitted, body, threads.Count);
				SubmittedReviews.Add((number, submitted, body));
			} else {
				PendingReviews[number] = new PendingReview($"review-{++_nextId}", body);
			}

			foreach (var draft in threads) {
				AddThread(number, draft, pending: verdict is null);
			}

			return Task.FromResult(PendingReviews.GetValueOrDefault(number)?.Id ?? $"review-{++_nextId}");
		}
	}

	/// <inheritdoc/>
	public Task AddThreadAsync(RepoRef repo, string reviewId, NewReviewComment draft, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(draft);
		lock (_gate) {
			AddThread(PendingNumber(reviewId), draft, pending: true);
			return Task.CompletedTask;
		}
	}

	/// <inheritdoc/>
	public Task ReplyAsync(RepoRef repo, string threadId, string? reviewId, string body, CancellationToken ct = default) {
		lock (_gate) {
			int index = _threads.FindIndex(t => t.Thread.Id == threadId);
			if (index < 0) {
				throw new InvalidOperationException($"Review thread {threadId} doesn't exist.");
			}

			var (number, thread) = _threads[index];
			if (reviewId is not null && PendingNumber(reviewId) != number) {
				throw new InvalidOperationException($"Review {reviewId} isn't on that thread's pull request.");
			}

			// GitHub files a reply into the viewer's pending review whether or not the call names it.
			_threads[index] = (number, thread with { Comments = [.. thread.Comments, Comment(body, PendingReviews.ContainsKey(number))] });
			return Task.CompletedTask;
		}
	}

	/// <inheritdoc/>
	public Task EditAsync(RepoRef repo, string commentId, string body, CancellationToken ct = default) {
		lock (_gate) {
			Rewrite(commentId, c => c with { Body = body, UpdatedAt = Now() });
			return Task.CompletedTask;
		}
	}

	/// <inheritdoc/>
	public Task DeleteCommentAsync(RepoRef repo, string commentId, CancellationToken ct = default) {
		lock (_gate) {
			Rewrite(commentId, _ => null);
			return Task.CompletedTask;
		}
	}

	/// <inheritdoc/>
	public Task SubmitReviewAsync(RepoRef repo, string reviewId, ReviewVerdict verdict, string body, CancellationToken ct = default) {
		lock (_gate) {
			int number = PendingNumber(reviewId);
			RequireVerdict(number, verdict, body, Drafts(number).Count());
			PendingReviews.Remove(number);
			MapDrafts(number, c => c with { Pending = false });
			SubmittedReviews.Add((number, verdict, body));
			return Task.CompletedTask;
		}
	}

	/// <inheritdoc/>
	public Task DiscardReviewAsync(RepoRef repo, string reviewId, CancellationToken ct = default) {
		lock (_gate) {
			int number = PendingNumber(reviewId);
			PendingReviews.Remove(number);
			MapDrafts(number, _ => null);
			return Task.CompletedTask;
		}
	}

	private static string PullRequestId(int number) => $"PR_{number}";

	private bool IsViewer(string login) => string.Equals(login, _viewer.Login, StringComparison.OrdinalIgnoreCase);

	private bool ViewerIsAuthor(int number) => _pullRequests.FirstOrDefault(p => p.Number == number) is { } pr && IsViewer(pr.Author);

	private int PendingNumber(string reviewId) =>
		PendingReviews.FirstOrDefault(p => p.Value.Id == reviewId) is { Value: not null } pending
			? pending.Key
			: throw new InvalidOperationException($"Could not resolve to a PullRequestReview with the id of '{reviewId}'.");

	private IEnumerable<ReviewComment> Drafts(int number) =>
		_threads.Where(t => t.Number == number).SelectMany(t => t.Thread.Comments).Where(c => c.Pending);

	private void RequireVerdict(int number, ReviewVerdict verdict, string body, int comments) {
		if (ViewerIsAuthor(number) && verdict == ReviewVerdict.Approve) {
			throw new InvalidOperationException("Can not approve your own pull request");
		}

		if (ViewerIsAuthor(number) && verdict == ReviewVerdict.RequestChanges) {
			throw new InvalidOperationException("Can not request changes on your own pull request");
		}

		if (verdict != ReviewVerdict.Approve && comments == 0 && string.IsNullOrWhiteSpace(body)) {
			throw new InvalidOperationException("You need to leave a comment indicating the requested changes.");
		}
	}

	private void AddThread(int number, NewReviewComment draft, bool pending) =>
		_threads.Add((number, new ReviewThread($"thread-{++_nextId}", draft.Path, draft.Line, "right", false, [Comment(draft.Body, pending)])));

	private ReviewComment Comment(string body, bool pending) {
		string now = Now();
		return new ReviewComment {
			Id = $"comment-{++_nextId}",
			Author = _viewer.Login,
			AuthorAvatarUrl = _viewer.AvatarUrl,
			Url = string.Empty,
			Body = body,
			CreatedAt = now,
			UpdatedAt = now,
			Pending = pending,
			Mine = true,
		};
	}

	// Maps one comment (null removes it); a thread left without comments goes with it.
	private void Rewrite(string commentId, Func<ReviewComment, ReviewComment?> change) {
		int index = _threads.FindIndex(t => t.Thread.Comments.Any(c => c.Id == commentId));
		if (index < 0) {
			throw new InvalidOperationException($"Review comment {commentId} doesn't exist.");
		}

		Replace(index, c => c.Id == commentId ? change(c) : c);
	}

	private void MapDrafts(int number, Func<ReviewComment, ReviewComment?> change) {
		for (int i = _threads.Count - 1; i >= 0; i--) {
			if (_threads[i].Number == number) {
				Replace(i, c => c.Pending ? change(c) : c);
			}
		}
	}

	private void Replace(int index, Func<ReviewComment, ReviewComment?> change) {
		var (number, thread) = _threads[index];
		ReviewComment[] comments = [.. thread.Comments.Select(change).OfType<ReviewComment>()];
		if (comments.Length == 0) {
			_threads.RemoveAt(index);
		} else {
			_threads[index] = (number, thread with { Comments = comments });
		}
	}

	private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
