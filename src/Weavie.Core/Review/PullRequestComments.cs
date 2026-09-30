using Weavie.Core.Commands;
using Weavie.Core.Git;

namespace Weavie.Core.Review;

/// <summary>
/// Owns one session's live PR review comments: loads them for the branch's current PR, validates and posts
/// comments/replies/edits against the exact head the user saw, and publishes each snapshot. Every load and post
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
	private IReadOnlyList<ReviewComment> _comments = [];
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

	/// <summary>Posts a new comment on PR-head <paramref name="line"/> of the repository-relative <paramref name="path"/>.</summary>
	public Task<CommandResult> CommentAsync(int number, string headSha, string path, int line, string body, CancellationToken ct) =>
		MutateAsync(
			number,
			body,
			set => set.HeadSha != headSha ? Stale(number)
				: !set.ChangedPaths.Contains(path, StringComparer.Ordinal) ? $"{path} isn't changed in PR #{number}."
				: line < 1 ? "Comments anchor to a line of the file." : null,
			target => _store.AddAsync(
				target.Repo, number, headSha, new NewReviewComment { Path = path, Line = line, Body = body }, ct),
			ct);

	/// <summary>Replies to the thread rooted at <paramref name="inReplyTo"/>.</summary>
	public Task<CommandResult> ReplyAsync(int number, long inReplyTo, string body, CancellationToken ct) =>
		MutateAsync(
			number,
			body,
			set => set.Threads.Any(t => t.RootId == inReplyTo) ? null : $"That thread is no longer on PR #{number}.",
			target => _store.ReplyAsync(target.Repo, number, inReplyTo, body, ct),
			ct);

	/// <summary>Replaces the body of the viewer's own comment <paramref name="id"/>.</summary>
	public Task<CommandResult> EditAsync(int number, long id, string body, CancellationToken ct) =>
		MutateAsync(
			number,
			body,
			set => set.Threads.SelectMany(t => t.Comments).FirstOrDefault(c => c.Id == id) is not { } comment
				? $"That comment is no longer on PR #{number}."
				: set.IsMine(comment) ? null : "You can only edit your own comments.",
			target => _store.EditAsync(target.Repo, id, body, ct),
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
		string body,
		Func<PullRequestCommentSet, string?> validate,
		Func<PullRequestTarget, Task<ReviewComment>> post,
		CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(body)) {
			return CommandResult.Failure("A comment can't be empty.");
		}

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			if (_latest.Set is not { } set || _target is not { } target || set.Number != number || target.Number != number) {
				return CommandResult.Failure($"PR #{number} isn't this session's pull request.");
			}

			if ((target.HeadSha != set.HeadSha ? Stale(number) : validate(set)) is { } rejection) {
				return CommandResult.Failure(rejection);
			}

			try {
				await post(target).ConfigureAwait(false);
			} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
				return CommandResult.Failure(ex.Message);
			}

			await RefreshLockedAsync(ct).ConfigureAwait(false);
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
			var comments = await _store.ListAsync(target.Repo, target.Number, ct).ConfigureAwait(false);
			string viewer = await _store.ViewerLoginAsync(target.Repo, ct).ConfigureAwait(false);
			if (_latest is { Error: null, Set: { } current } && current.Number == target.Number
				&& current.HeadSha == target.HeadSha && current.Viewer == viewer
				&& ReferenceEquals(current.ChangedPaths, diff.Paths) && _comments.SequenceEqual(comments)) {
				return;
			}

			_comments = comments;
			Publish(new PullRequestCommentsSnapshot(
				new PullRequestCommentSet(target.Number, target.Url, target.HeadSha, viewer, diff.Paths, ReviewThread.Group(comments)),
				null));
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

	// Commentable paths are the PR's own diff (merge-base..head), fetched once per head/base pair.
	private async Task<PullRequestDiff> DiffAsync(PullRequestTarget target, CancellationToken ct) {
		if (_diff is { } cached && cached.Number == target.Number && cached.HeadSha == target.HeadSha && cached.BaseRef == target.BaseRef) {
			return cached;
		}

		if (!GitService.IsCommitSha(target.HeadSha) || !GitService.IsValidBranchName(target.BaseRef)) {
			throw new InvalidOperationException($"the forge reported head '{target.HeadSha}' onto '{target.BaseRef}'.");
		}

		if (await _git.ResolveCommitAsync(_worktree, target.HeadSha, ct).ConfigureAwait(false) is null) {
			await _git.FetchAsync(_worktree, target.Remote, $"pull/{target.Number}/head", ct).ConfigureAwait(false);
			_ = await _git.ResolveCommitAsync(_worktree, target.HeadSha, ct).ConfigureAwait(false)
				?? throw new InvalidOperationException($"its head {target.HeadSha[..7]} isn't on '{target.Remote}'.");
		}

		await _git.FetchAsync(_worktree, target.Remote, target.BaseRef, ct).ConfigureAwait(false);
		string mergeBase = await _git.MergeBaseAsync(_worktree, $"{target.Remote}/{target.BaseRef}", target.HeadSha, ct).ConfigureAwait(false)
			?? throw new InvalidOperationException($"its head shares no history with '{target.Remote}/{target.BaseRef}'.");
		var changes = await _git.DiffRefsAsync(_worktree, mergeBase, target.HeadSha, ct).ConfigureAwait(false);
		return _diff = new PullRequestDiff(target.Number, target.HeadSha, target.BaseRef, mergeBase, [.. changes.Select(c => c.Path)]);
	}

	private static string Stale(int number) => $"PR #{number} has new commits — comments reloaded; try again.";

	private sealed record PullRequestDiff(int Number, string HeadSha, string BaseRef, string MergeBase, IReadOnlyList<string> Paths);
}
