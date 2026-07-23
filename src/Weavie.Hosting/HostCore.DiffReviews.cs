using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// The review-diff surface: PR and local-ref producers seed the session-owned change tracker, then use the
// same keep/revert/history engine and turn messages as an ordinary post-turn review.
public sealed partial class HostCore {
	private readonly ConcurrentDictionary<ReviewCommentCacheKey, IReadOnlyList<ReviewComment>> _reviewCommentCache = [];
	private readonly ConcurrentDictionary<ReviewCommentCacheKey, long> _reviewCommentRequests = [];
	private readonly ConcurrentDictionary<ReviewCommentCacheKey, CancellationTokenSource> _reviewCommentLifetimes = [];
	private long _reviewCommentRequestSequence;

	/// <summary>Arms a local ref review over the active session's working tree.</summary>
	private async Task DiffAgainstFromWebAsync(string reference) {
		reference = reference.Trim();
		if (reference.Length == 0 || _session is not { } session) {
			return;
		}

		long token = session.Changes.BeginReviewArm();
		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		ReviewIdentity review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, CancellationToken.None).ConfigureAwait(false) is not { } target) {
				Notify("warn", $"'{reference}' isn't a branch, tag, or commit here.");
				return;
			}

			string head = await git.GetHeadCommitAsync(worktree, CancellationToken.None).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, CancellationToken.None).ConfigureAwait(false) is not { } mergeBase) {
				Notify("warn", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return;
			}

			review = new ReviewIdentity(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			changes = await ComputeReviewChangesAsync(review).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify("warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return;
		}

		if (changes.Count == 0) {
			Notify("info", $"No changes against '{reference}'.");
			RetractActiveReview(session, token);
			return;
		}

		await SeedAndArmReviewAsync(review, session, changes, token).ConfigureAwait(false);
	}

	/// <summary>Builds every seed before atomically replacing and checkpointing the tracker board.</summary>
	private async Task SeedAndArmReviewAsync(
		ReviewIdentity review,
		HostSession session,
		IReadOnlyList<DiffFileChange> changes,
		long token) {
		var git = new GitService();
		var seeds = new List<ReviewSeed>(changes.Count);
		try {
			foreach (var change in changes) {
				string absolute = Path.GetFullPath(Path.Combine(review.Worktree, change.Path));
				if (!TryReadWorktreeText(session, absolute, out string diskContent, out bool diskExists)) {
					continue;
				}

				var atRef = await git.ReadFileAtRefAsync(
					review.Worktree, review.MergeBase, change.Path, CancellationToken.None).ConfigureAwait(false);
				if (atRef.Exists && !atRef.IsText) {
					continue;
				}

				seeds.Add(new ReviewSeed(absolute, atRef.Content, diskContent, atRef.Exists, diskExists));
			}
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			RemoveReviewComments(session, review, token);
			Log($"[weavie] review '{review.Label}': seed failed: {ex.Message}");
			Notify("warn", $"Couldn't arm {review.Label}: {ex.Message}");
			return;
		}
		if (seeds.Count == 0) {
			Notify("info", $"No text changes to review ({review.Label}).");
			RetractActiveReview(session, token);
			return;
		}

		_ui.Post(() => {
			if (!IsLoadedSession(session)) {
				RemoveReviewComments(session, review, token);
				return;
			}

			try {
				if (!session.Changes.ArmReview(token, review, seeds)) {
					RemoveReviewComments(session, review, token);
					if (session.Changes.ArmToken == token) {
						Notify("warn", $"Couldn't arm {review.Label} because a changed file moved while it was loading.");
					}
					return;
				}
			} catch (ReviewPersistenceException ex) {
				RemoveReviewComments(session, review, token);
				NotifyReviewProblem(session, ex.Message);
				return;
			} catch (ArgumentException ex) {
				RemoveReviewComments(session, review, token);
				Notify("error", $"Couldn't arm {review.Label}: {ex.Message}");
				return;
			}

			PruneReviewComments(session, review, token);
			DispatchEditorProjection(session, () => {
				_bridge.PostToWeb(ChangeMessages.TurnReset());
				PushTurnChangesToWeb();
				PushReviewHistoryToWeb();
				string first = seeds[0].Path;
				int line = session.Changes.GetTurn(first) is { } turn
					? LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText) ?? 1
					: 1;
				session.FileOpener.Open(first, line, preview: true, scratch: false);
				PushReviewFileToWeb(first);
			});
		});
	}

	private static Task<IReadOnlyList<DiffFileChange>> ComputeReviewChangesAsync(ReviewIdentity review) =>
		review.PrNumber > 0
			? new GitService().DiffRefsAsync(review.Worktree, review.MergeBase, review.HeadRef, CancellationToken.None)
			: new GitService().DiffWorktreeAsync(review.Worktree, review.MergeBase, CancellationToken.None);

	private static bool TryReadWorktreeText(
		HostSession session,
		string absolutePath,
		out string content,
		out bool exists) {
		exists = session.FileSystem.FileExists(absolutePath);
		content = string.Empty;
		return !exists || session.FileSystem.TryReadAllText(absolutePath, out content);
	}

	private void RetractActiveReview(HostSession session, long token) {
		_ui.Post(() => {
			if (!IsLoadedSession(session)) {
				return;
			}

			try {
				if (!session.Changes.RetractReview(token)) {
					return;
				}
			} catch (ReviewPersistenceException ex) {
				NotifyReviewProblem(session, ex.Message);
				return;
			}

			PurgeReviewComments(session);
			DispatchEditorProjection(session, () => {
				_bridge.PostToWeb(ChangeMessages.TurnReset());
				PushTurnChangesToWeb();
				PushReviewHistoryToWeb();
			});
		});
	}

	private void PushReviewFileToWeb(string absolutePath) {
		if (_session is not { } session || session.Changes.GetTurn(absolutePath) is null) {
			return;
		}

		if (session.Changes.ActiveReviewIdentity is { } review) {
			PushReviewCommentsToWeb(session, review, session.Changes.ActiveReviewToken, absolutePath);
		}

		PushTurnDiffToWeb(absolutePath);
	}

	private void PushReviewCommentsToWeb(HostSession session, ReviewIdentity review, long token, string absolutePath) {
		if (review.PrNumber == 0) {
			return;
		}

		var comments = _reviewCommentCache.GetValueOrDefault(ReviewCommentKey(session, review, token), []);
		string relative = Path.GetRelativePath(review.Worktree, absolutePath).Replace('\\', '/');
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "review-comments",
			number = review.PrNumber,
			path = absolutePath,
			comments = comments
				.Where(comment => string.Equals(comment.Path, relative, StringComparison.Ordinal))
				.Select(comment => new {
					id = comment.Id,
					line = comment.Line,
					side = comment.Side,
					author = comment.Author,
					body = comment.Body,
					createdAt = comment.CreatedAt,
					inReplyTo = comment.InReplyTo,
				}),
		}));
	}

	private void SurfaceActiveReviewOnSwitch() {
		if (_session is not { } session || session.Changes.ActiveReviewIdentity is null) {
			return;
		}

		var changes = session.Changes.TurnChanges();
		if (changes.Count == 0) {
			return;
		}

		var first = changes[0];
		int line = LineDiff.FirstChangedLine(first.BaselineText, first.CurrentText) ?? 1;
		session.FileOpener.Open(first.Path, line, preview: true, scratch: false);
		PushReviewFileToWeb(first.Path);
	}

	private ReviewIdentity? ActiveReview() => _session?.Changes.ActiveReviewIdentity;

	private static bool ReviewStillArmed(HostSession session, ReviewIdentity review, long token) =>
		session.Changes.ActiveReviewToken == token && session.Changes.ActiveReviewIdentity == review;

	private ReviewCommentRefresh BeginReviewCommentRefresh(HostSession session, ReviewIdentity review, long token) {
		var key = ReviewCommentKey(session, review, token);
		long request = Interlocked.Increment(ref _reviewCommentRequestSequence);
		_reviewCommentRequests[key] = request;
		return new ReviewCommentRefresh(key, request, AcquireReviewCommentCancellation(session, review, token, key));
	}

	private CancellationToken AcquireReviewCommentCancellation(
		HostSession session,
		ReviewIdentity review,
		long token,
		ReviewCommentCacheKey key) {
		var lifetime = _reviewCommentLifetimes.GetOrAdd(key, static _ => new CancellationTokenSource());
		if (IsLoadedSession(session)
			&& (session.Changes.ArmToken == token || ReviewStillArmed(session, review, token))) {
			return lifetime.Token;
		}

		RemoveReviewComments(key);
		return new CancellationToken(canceled: true);
	}

	private Task CommitReviewCommentsAsync(
		HostSession session,
		ReviewIdentity review,
		long token,
		ReviewCommentRefresh refresh,
		IReadOnlyList<ReviewComment> comments) {
		var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ui.Post(() => {
			if (IsLoadedSession(session)
				&& (_reviewCommentRequests.GetValueOrDefault(refresh.Key) == refresh.Request)
				&& (session.Changes.ArmToken == token || ReviewStillArmed(session, review, token))) {
				_reviewCommentCache[refresh.Key] = [.. comments];
			}
			completion.SetResult();
		});
		return completion.Task;
	}

	private void PruneReviewComments(HostSession session, ReviewIdentity review, long token) {
		var keep = ReviewCommentKey(session, review, token);
		PurgeReviewComments(key =>
			string.Equals(key.SessionId, session.Id, StringComparison.Ordinal) && key != keep);
	}

	private void RemoveReviewComments(HostSession session, ReviewIdentity review, long token) =>
		RemoveReviewComments(ReviewCommentKey(session, review, token));

	private void PurgeReviewComments(HostSession session) => PurgeReviewComments(
		key => string.Equals(key.SessionId, session.Id, StringComparison.Ordinal));

	private void PurgeReviewComments(string worktree) => PurgeReviewComments(
		key => string.Equals(key.Worktree, worktree, StringComparison.Ordinal));

	private void PurgeReviewComments(Func<ReviewCommentCacheKey, bool> includes) {
		var keys = _reviewCommentCache.Keys
			.Concat(_reviewCommentRequests.Keys)
			.Concat(_reviewCommentLifetimes.Keys)
			.Where(includes)
			.Distinct()
			.ToArray();
		foreach (var key in keys) {
			RemoveReviewComments(key);
		}
	}

	private void RemoveReviewComments(ReviewCommentCacheKey key) {
		_reviewCommentCache.TryRemove(key, out _);
		_reviewCommentRequests.TryRemove(key, out _);
		if (_reviewCommentLifetimes.TryRemove(key, out var lifetime)) {
			// Leave the canceled source for collection so concurrent token reads cannot race disposal.
			lifetime.Cancel();
		}
	}

	private static ReviewCommentCacheKey ReviewCommentKey(HostSession session, ReviewIdentity review, long token) =>
		new(session.Id, review.Worktree, token, review.PrNumber, review.HeadSha);

	private readonly record struct ReviewCommentCacheKey(
		string SessionId,
		string Worktree,
		long Token,
		int PrNumber,
		string HeadSha);

	private readonly record struct ReviewCommentRefresh(
		ReviewCommentCacheKey Key,
		long Request,
		CancellationToken Cancellation);
}
