namespace Weavie.Core.Review;

/// <summary>
/// An in-memory <see cref="IPullRequestProvider"/> + <see cref="IReviewCommentStore"/> — the deterministic
/// stand-in for the headless integration harness and the capture recording, so a PR journey (list, diff,
/// comment, reply) never touches the network. Adds/replies append to the in-memory thread and echo back, so the
/// UI round-trips exactly as it would against the real forge.
/// </summary>
public sealed class StaticPullRequestProvider : IPullRequestProvider, IReviewCommentStore {
	private readonly IReadOnlyList<PullRequestSummary> _pullRequests;
	private readonly List<ReviewComment> _comments;
	private readonly object _gate = new();
	private long _nextId;

	/// <summary>Creates a provider seeded with <paramref name="pullRequests"/> and <paramref name="comments"/>.</summary>
	public StaticPullRequestProvider(IReadOnlyList<PullRequestSummary> pullRequests, IReadOnlyList<ReviewComment> comments) {
		ArgumentNullException.ThrowIfNull(pullRequests);
		ArgumentNullException.ThrowIfNull(comments);
		_pullRequests = pullRequests;
		_comments = [.. comments];
		_nextId = comments.Count == 0 ? 1 : comments.Max(c => c.Id) + 1;
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(_pullRequests);
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) {
		lock (_gate) {
			return Task.FromResult<IReadOnlyList<ReviewComment>>([.. _comments]);
		}
	}

	/// <inheritdoc/>
	public Task<ReviewComment> AddAsync(RepoRef repo, int number, string commitId, NewReviewComment draft, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(draft);
		var comment = Append(draft.Path, draft.Line, draft.Side, draft.Body, inReplyTo: 0);
		return Task.FromResult(comment);
	}

	/// <inheritdoc/>
	public Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string replyBody, CancellationToken ct = default) {
		lock (_gate) {
			var parent = _comments.FirstOrDefault(c => c.Id == inReplyTo);
			var comment = Append(parent?.Path ?? string.Empty, parent?.Line ?? 1, parent?.Side ?? "right", replyBody, inReplyTo);
			return Task.FromResult(comment);
		}
	}

	private ReviewComment Append(string path, int line, string side, string body, long inReplyTo) {
		lock (_gate) {
			var comment = new ReviewComment {
				Id = _nextId++,
				Path = path,
				Line = line,
				Side = side,
				Author = "you",
				Body = body,
				CreatedAt = "now",
				InReplyTo = inReplyTo,
			};
			_comments.Add(comment);
			return comment;
		}
	}
}
