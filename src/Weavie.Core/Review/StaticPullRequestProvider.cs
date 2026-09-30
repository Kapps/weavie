namespace Weavie.Core.Review;

/// <summary>
/// An in-memory <see cref="IPullRequestProvider"/> + <see cref="IReviewCommentStore"/> — the deterministic
/// stand-in for the headless integration harness and the capture recording, so a PR journey (list, diff,
/// comment, reply, edit) never touches the network. Adds/replies append as <c>viewer</c> and echo back, so the UI
/// round-trips exactly as it would against the real forge.
/// </summary>
public sealed class StaticPullRequestProvider : IPullRequestProvider, IReviewCommentStore {
	private readonly IReadOnlyList<PullRequestSummary> _pullRequests;
	private readonly List<(int Number, ReviewComment Comment)> _comments;
	private readonly string _viewer;
	private readonly Lock _gate = new();
	private long _nextId;

	/// <summary>
	/// Creates a provider seeded with <paramref name="pullRequests"/> and each PR's <paramref name="comments"/>,
	/// authenticated as <paramref name="viewer"/>.
	/// </summary>
	public StaticPullRequestProvider(
		IReadOnlyList<PullRequestSummary> pullRequests,
		IReadOnlyList<(int Number, ReviewComment Comment)> comments,
		string viewer) {
		ArgumentNullException.ThrowIfNull(pullRequests);
		ArgumentNullException.ThrowIfNull(comments);
		ArgumentException.ThrowIfNullOrWhiteSpace(viewer);
		_pullRequests = pullRequests;
		_comments = [.. comments];
		_viewer = viewer;
		_nextId = comments.Count == 0 ? 1 : comments.Max(c => c.Comment.Id) + 1;
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult<IReadOnlyList<PullRequestSummary>>(
			[.. _pullRequests.Where(p => p.State == PullRequestState.Open)]);
	}

	/// <inheritdoc/>
	public Task<PullRequestSummary?> FindForBranchAsync(RepoRef repo, string headOwner, string branch, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		var matches = _pullRequests.Where(p => p.HeadRef.Equals(branch, StringComparison.Ordinal));
		return Task.FromResult(
			matches.FirstOrDefault(p => p.State == PullRequestState.Open) ?? matches.FirstOrDefault());
	}

	/// <inheritdoc/>
	public Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string q = query.Trim();
		var open = _pullRequests.Where(p => p.State == PullRequestState.Open);
		if (q.Length == 0) {
			return Task.FromResult<IReadOnlyList<PullRequestSummary>>([.. open]);
		}

		var matches = open
			.Where(p => $"#{p.Number} {p.Title} {p.Author} {p.HeadRef}".Contains(q, StringComparison.OrdinalIgnoreCase))
			.ToList();
		return Task.FromResult<IReadOnlyList<PullRequestSummary>>(matches);
	}

	/// <inheritdoc/>
	public Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(_pullRequests.FirstOrDefault(p => p.Number == number));
	}

	/// <summary>
	/// Commit sha → pull-request number, seeded by the harness so a blamed line resolves to a PR offline. A sha
	/// with no entry has no pull request, exactly as the forge would report.
	/// </summary>
	public Dictionary<string, int> PullRequestsByCommit { get; } = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public Task<PullRequestSummary?> FindForCommitAsync(RepoRef repo, string sha, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		return Task.FromResult(PullRequestsByCommit.TryGetValue(sha, out int number)
			? _pullRequests.FirstOrDefault(p => p.Number == number)
			: null);
	}

	/// <inheritdoc/>
	public string CommitUrl(RepoRef repo, string sha) => GitHubReviewProvider.WebCommitUrl(repo, sha);

	/// <inheritdoc/>
	public string RefUrlBase(RepoRef repo) => GitHubReviewProvider.WebRefUrlBase(repo);

	/// <inheritdoc/>
	public Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) {
		lock (_gate) {
			return Task.FromResult<IReadOnlyList<ReviewComment>>([.. _comments.Where(c => c.Number == number).Select(c => c.Comment)]);
		}
	}

	/// <inheritdoc/>
	public Task<ReviewComment> AddAsync(RepoRef repo, int number, string commitId, NewReviewComment draft, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(draft);
		return Task.FromResult(Append(number, draft.Path, draft.Line, "right", draft.Body, 0));
	}

	/// <inheritdoc/>
	public Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string body, CancellationToken ct = default) {
		lock (_gate) {
			var parent = Find(number, inReplyTo);
			return Task.FromResult(Append(number, parent.Path, parent.Line, parent.Side, body, inReplyTo));
		}
	}

	/// <inheritdoc/>
	public Task<ReviewComment> EditAsync(RepoRef repo, long id, string body, CancellationToken ct = default) {
		lock (_gate) {
			int index = _comments.FindIndex(c => c.Comment.Id == id);
			if (index < 0) {
				throw new InvalidOperationException($"Review comment {id} doesn't exist.");
			}

			var edited = _comments[index] with { Comment = _comments[index].Comment with { Body = body, UpdatedAt = Now() } };
			_comments[index] = edited;
			return Task.FromResult(edited.Comment);
		}
	}

	/// <inheritdoc/>
	public Task<string> ViewerLoginAsync(RepoRef repo, CancellationToken ct = default) => Task.FromResult(_viewer);

	private ReviewComment Find(int number, long id) =>
		_comments.FirstOrDefault(c => c.Number == number && c.Comment.Id == id).Comment
			?? throw new InvalidOperationException($"Review comment {id} isn't on PR #{number}.");

	private ReviewComment Append(int number, string path, int line, string side, string body, long inReplyTo) {
		lock (_gate) {
			string now = Now();
			var comment = new ReviewComment {
				Id = _nextId++,
				Path = path,
				Line = line,
				Outdated = false,
				Side = side,
				Author = _viewer,
				Body = body,
				CreatedAt = now,
				UpdatedAt = now,
				InReplyTo = inReplyTo,
			};
			_comments.Add((number, comment));
			return comment;
		}
	}

	private static string Now() => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
