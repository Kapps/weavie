using System.Text.Json.Serialization;
using Weavie.Core.Commands;
using Weavie.Core.Git;

namespace Weavie.Core.Review;

/// <summary>
/// Owns one session's live PR review comments: loads them for the branch's current PR, validates and writes
/// comments, drafts, and reviews against the exact head the user saw, and publishes each snapshot. Every load and post
/// runs behind one gate and publishes inside it, so the last published snapshot is always the newest.
/// </summary>
public sealed class PullRequestComments {
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly GitService _git = new();
	private readonly string _worktree;
	private readonly IReviewCommentStore _store;
	private readonly Action<PullRequestCommentsSnapshot> _publish;
	private volatile PullRequestTarget? _target;
	private volatile bool _tracking;
	private volatile PullRequestDiff? _diff;
	private volatile PullRequestCommentsSnapshot _latest = new(null, null);

	/// <summary>Creates the comments owner for the git worktree at <paramref name="worktree"/>.</summary>
	public PullRequestComments(string worktree, IReviewCommentStore store, Action<PullRequestCommentsSnapshot> publish) {
		ArgumentException.ThrowIfNullOrEmpty(worktree);
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(publish);
		_worktree = worktree;
		_store = store;
		_publish = publish;
	}

	/// <summary>The most recently published snapshot.</summary>
	public PullRequestCommentsSnapshot Latest => _latest;

	/// <summary>
	/// Follows the branch's PR (null: none). Comments reload only when the PR or its head/base changes — a push
	/// moves their lines — never merely because the branch was re-checked.
	/// </summary>
	public Task TrackAsync(PullRequestTarget? target, CancellationToken ct) {
		if (_tracking && _target == target) {
			return Task.CompletedTask;
		}

		_tracking = true;
		_target = target;
		return RefreshAsync(ct);
	}

	/// <summary>Re-reads the current PR's comments, publishing only when something changed.</summary>
	public async Task RefreshAsync(CancellationToken ct) {
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await RefreshLockedAsync(ct).ConfigureAwait(false);
		} finally {
			_gate.Release();
		}
	}

	/// <summary>
	/// Comments on PR-head <paramref name="line"/> of the repository-relative <paramref name="path"/>: posted now, or
	/// queued into the viewer's pending review (starting one when there's none).
	/// </summary>
	public Task<CommandResult> CommentAsync(int number, string headSha, string path, int line, string body, CommentMode mode, CancellationToken ct) {
		var draft = new NewReviewComment { Path = path, Line = line, Body = body };
		return MutateAsync(
			number,
			set => RequireBody(body) ?? (set.HeadSha != headSha ? Stale(number)
				: !set.ChangedPaths.Contains(path, StringComparer.Ordinal) ? $"{path} isn't changed in PR #{number}."
				: line < 1 ? "Comments anchor to a line of the file." : RequireMode(set, mode)),
			(target, review) => mode == CommentMode.Now
				? _store.CreateReviewAsync(target.Repo, review.PullRequestId, headSha, ReviewVerdict.Comment, string.Empty, [draft], ct)
				: review.Pending is { } pending ? _store.AddThreadAsync(target.Repo, pending.Id, draft, ct)
				: _store.CreateReviewAsync(target.Repo, review.PullRequestId, headSha, null, string.Empty, [draft], ct),
			ct);
	}

	/// <summary>Replies to thread <paramref name="threadId"/>: posted now, or queued into the viewer's pending review.</summary>
	public Task<CommandResult> ReplyAsync(int number, string threadId, string body, CommentMode mode, CancellationToken ct) =>
		MutateAsync(
			number,
			set => RequireBody(body) ?? (set.Review.Threads.Any(t => t.Id == threadId)
				? RequireMode(set, mode)
				: $"That thread is no longer on PR #{number}."),
			async (target, review) => await _store.ReplyAsync(
				target.Repo,
				threadId,
				mode == CommentMode.Now ? null
					: review.Pending?.Id ?? await _store.CreateReviewAsync(
						target.Repo, review.PullRequestId, target.HeadSha, null, string.Empty, [], ct).ConfigureAwait(false),
				body,
				ct).ConfigureAwait(false),
			ct);

	/// <summary>Replaces the body of the viewer's own comment <paramref name="id"/>, a draft or a posted one.</summary>
	public Task<CommandResult> EditAsync(int number, string id, string body, CancellationToken ct) =>
		MutateAsync(
			number,
			set => RequireBody(body) ?? (Find(set, id) is not { } comment ? Gone(number)
				: comment.Mine ? null : "You can only edit your own comments."),
			(target, _) => _store.EditAsync(target.Repo, id, body, ct),
			ct);

	/// <summary>Deletes draft <paramref name="id"/> from the viewer's pending review.</summary>
	public Task<CommandResult> DiscardCommentAsync(int number, string id, CancellationToken ct) =>
		MutateAsync(
			number,
			set => Find(set, id) is not { } comment ? Gone(number)
				: comment is { Mine: true, Pending: true } ? null : "Only your pending comments can be discarded.",
			(target, _) => _store.DeleteCommentAsync(target.Repo, id, ct),
			ct);

	/// <summary>
	/// Submits the viewer's review of the head they saw, <paramref name="headSha"/>: their pending drafts with
	/// <paramref name="verdict"/> and summary <paramref name="body"/>, or just the verdict when nothing is pending.
	/// </summary>
	public Task<CommandResult> SubmitReviewAsync(int number, string headSha, ReviewVerdict verdict, string body, CancellationToken ct) =>
		MutateAsync(
			number,
			set => set.HeadSha != headSha ? Stale(number)
				: set.Review.ViewerIsAuthor && verdict != ReviewVerdict.Comment ? "You can only comment on your own pull request."
				: set.Review.Pending is null && verdict == ReviewVerdict.Comment && string.IsNullOrWhiteSpace(body)
					? "Write a summary or add a comment to submit a review." : null,
			(target, review) => review.Pending is { } pending
				? _store.SubmitReviewAsync(target.Repo, pending.Id, verdict, body, ct)
				: _store.CreateReviewAsync(target.Repo, review.PullRequestId, headSha, verdict, body, [], ct),
			ct);

	/// <summary>Deletes the viewer's pending review and every draft in it.</summary>
	public Task<CommandResult> DiscardReviewAsync(int number, CancellationToken ct) =>
		MutateAsync(
			number,
			set => set.Review.Pending is null ? $"You have no pending review on PR #{number}." : null,
			(target, review) => _store.DiscardReviewAsync(target.Repo, review.Pending!.Id, ct),
			ct);

	/// <summary>The repository-relative <paramref name="path"/> at <paramref name="headSha"/> and at its merge-base.</summary>
	public async Task<PullRequestSources> SourcesAsync(string path, string headSha, CancellationToken ct) {
		if (_diff is not { } diff || diff.HeadSha != headSha) {
			throw new InvalidOperationException("Those pull request sources are no longer current.");
		}

		var head = await _git.ReadFileAtRefAsync(_worktree, headSha, path, ct).ConfigureAwait(false);
		var mergeBase = await _git.ReadFileAtRefAsync(_worktree, diff.MergeBase, path, ct).ConfigureAwait(false);
		return new PullRequestSources(head.Exists ? head.Content : null, mergeBase.Exists ? mergeBase.Content : null);
	}

	private async Task<CommandResult> MutateAsync(
		int number,
		Func<PullRequestCommentSet, string?> validate,
		Func<PullRequestTarget, PullRequestReview, Task> post,
		CancellationToken ct) {
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			if (_latest.Set is not { } set || _target is not { } target || set.Number != number || target.Number != number) {
				return CommandResult.Failure($"PR #{number} isn't this session's pull request.");
			}

			if ((target.HeadSha != set.HeadSha ? Stale(number) : validate(set)) is { } rejection) {
				return CommandResult.Failure(rejection);
			}

			try {
				await post(target, set.Review).ConfigureAwait(false);
			} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
				return CommandResult.Failure(ex.Message);
			} finally {
				// A failed write may still have changed the forge (a review created before its reply failed).
				await RefreshLockedAsync(ct).ConfigureAwait(false);
			}

			return CommandResult.Success();
		} finally {
			_gate.Release();
		}
	}

	private async Task RefreshLockedAsync(CancellationToken ct) {
		if (_target is not { } target) {
			Publish(new PullRequestCommentsSnapshot(null, null));
			return;
		}

		try {
			var diff = await DiffAsync(target, ct).ConfigureAwait(false);
			var review = await _store.LoadAsync(target.Repo, target.Number, ct).ConfigureAwait(false);
			var set = new PullRequestCommentSet(target.Number, target.Url, target.HeadSha, diff.Paths, review);
			if (_latest is not { Error: null, Set: { } current } || current != set) {
				Publish(new PullRequestCommentsSnapshot(set, null));
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			var lastGood = _latest.Set is { } set && set.Number == target.Number ? set : null;
			Publish(new PullRequestCommentsSnapshot(lastGood, $"Couldn't load PR #{target.Number}'s comments: {ex.Message}"));
		}
	}

	private void Publish(PullRequestCommentsSnapshot snapshot) {
		_latest = snapshot;
		_publish(snapshot);
	}

	// Commentable paths are the PR's own diff (merge-base..head), computed once per head/base pair.
	private async Task<PullRequestDiff> DiffAsync(PullRequestTarget target, CancellationToken ct) {
		if (_diff is { } cached && cached.Number == target.Number && cached.HeadSha == target.HeadSha && cached.BaseSha == target.BaseSha) {
			return cached;
		}

		await _git.FetchCommitAsync(_worktree, target.Remote, target.HeadSha, ct).ConfigureAwait(false);
		await _git.FetchCommitAsync(_worktree, target.Remote, target.BaseSha, ct).ConfigureAwait(false);
		string mergeBase = await _git.MergeBaseAsync(_worktree, target.BaseSha, target.HeadSha, ct).ConfigureAwait(false)
			?? throw new InvalidOperationException($"its head shares no history with its base {target.BaseSha[..7]}.");
		var changes = await _git.DiffRefsAsync(_worktree, mergeBase, target.HeadSha, ct).ConfigureAwait(false);
		return _diff = new PullRequestDiff(target.Number, target.HeadSha, target.BaseSha, mergeBase, [.. changes.Select(c => c.Path)]);
	}

	private static string Stale(int number) => $"PR #{number} has new commits — comments reloaded; try again.";

	private static string Gone(int number) => $"That comment is no longer on PR #{number}.";

	private static string? RequireBody(string body) => string.IsNullOrWhiteSpace(body) ? "A comment can't be empty." : null;

	// The forge files every comment into a pending review while one exists, so nothing can post alongside it.
	private static string? RequireMode(PullRequestCommentSet set, CommentMode mode) =>
		mode == CommentMode.Now && set.Review.Pending is not null ? "You have a pending review — new comments join it." : null;

	private static ReviewComment? Find(PullRequestCommentSet set, string id) =>
		set.Review.Threads.SelectMany(t => t.Comments).FirstOrDefault(c => c.Id == id);

	private sealed record PullRequestDiff(int Number, string HeadSha, string BaseSha, string MergeBase, IReadOnlyList<string> Paths);
}

/// <summary>Whether a new comment posts immediately or joins the viewer's pending review.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommentMode>))]
public enum CommentMode {
	/// <summary>Post it now, as a single comment.</summary>
	[JsonStringEnumMemberName("now")]
	Now,

	/// <summary>Queue it into the viewer's pending review, starting one when there's none.</summary>
	[JsonStringEnumMemberName("review")]
	Review,
}
