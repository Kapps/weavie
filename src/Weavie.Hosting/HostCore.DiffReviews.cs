using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// The review-diff surface: a session's worktree diffed against a base commit, reviewed through the SAME inline
// accept/reject engine as a turn (HostCore.WebBridge.cs). Fed by two producers — an opened pull request
// (HostCore.PullRequests.cs) and the local "diff against <ref>" command — which both SEED the session's change
// tracker from the merge-base and let keep/revert + accumulating new-turn edits flow through the shared
// turn-changes / turn-diff messages. See docs/specs/diff-against.md.
public sealed partial class HostCore {
	// Each session's armed review, keyed by worktree path — stable across switches, unlike the session's
	// path-hashed Id, and unique per session. One review per session; arming a new one replaces the old.
	private readonly ConcurrentDictionary<string, DiffReview> _diffReviews = new(StringComparer.Ordinal);

	/// <summary>
	/// Arms a "diff against &lt;ref&gt;" review on the active session: resolves the ref to a commit, diffs the
	/// working tree from its merge-base with HEAD (so a branch shows only this side's changes), and seeds the
	/// change tracker so the diff reviews through the same accept/reject engine as a turn. Failures surface as
	/// toasts; an empty diff says so and retracts any prior review instead of arming an unwalkable navigator.
	/// </summary>
	private async Task DiffAgainstFromWebAsync(string reference) {
		reference = reference.Trim();
		if (reference.Length == 0 || _session is not { } session) {
			return;
		}

		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		DiffReview review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, CancellationToken.None).ConfigureAwait(false) is not { } target) {
				Notify("error", $"'{reference}' isn't a branch, tag, or commit here.");
				return;
			}

			string head = await git.GetHeadCommitAsync(worktree, CancellationToken.None).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, CancellationToken.None).ConfigureAwait(false) is not { } mergeBase) {
				Notify("error", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return;
			}

			review = new DiffReview(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			changes = await ComputeReviewChangesAsync(review).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify("error", $"Couldn't diff against '{reference}': {ex.Message}");
			return;
		}

		if (changes.Count == 0) {
			// Nothing to review: answer where the user is (a toast), and retract any prior review so a stale walk
			// can't sit under the "no changes" answer. Retracting commits the tracker's board — but an empty diff
			// means the worktree equals the ref, so there are no pending edits to lose.
			Notify("info", $"No changes against '{reference}'.");
			if (_diffReviews.TryRemove(worktree, out _)) {
				RetractActiveReview(session);
			}

			return;
		}

		await SeedAndArmReviewAsync(review, session, changes).ConfigureAwait(false);
	}

	/// <summary>
	/// Seeds the session's change tracker from <paramref name="review"/>'s base→current diff, so the review (a PR
	/// or a local ref) runs through the same inline accept/reject engine as a turn: each file's baseline is its
	/// content at the merge-base, its current the worktree file. Records the review, pushes the review set + the
	/// first file's diff (+ comments for a PR), and opens that file (a review surfaces its code — post-turn review
	/// parks). Later hunk steps render lazily via <c>get-turn-diff</c>. A diff read failing toasts, leaving the
	/// session usable.
	/// </summary>
	private async Task SeedAndArmReviewAsync(DiffReview review, HostSession session, IReadOnlyList<DiffFileChange> changes) {
		// Record the review up front so a rapid re-arm (a second diff-against / PR open) replaces it here; the
		// guarded post below then sees a different ActiveReview() and bails, so a stale arm can't seed onto the
		// now-active review.
		_diffReviews[review.Worktree] = review;

		var git = new GitService();
		var seeds = new List<(string Absolute, string RefContent, string Disk, bool ExistedAtRef)>();
		try {
			foreach (var change in changes) {
				string absolute = Path.GetFullPath(Path.Combine(review.Worktree, change.Path));
				// The file at the merge-base is the review baseline; a non-empty result means it existed there, so a
				// revert of its last hunk truncates (an added-in-review file has an empty base ⇒ the revert deletes it).
				string refContent = await git.ShowFileAtRefAsync(review.Worktree, review.MergeBase, change.Path, CancellationToken.None).ConfigureAwait(false);
				string disk = await ReadWorktreeAsync(absolute).ConfigureAwait(false);
				seeds.Add((absolute, refContent, disk, refContent.Length > 0));
			}
		} catch (GitException ex) {
			Log($"[weavie] review '{review.Label}': diff failed: {ex.Message}");
			Notify("warn", $"Armed the review, but couldn't compute its diff: {ex.Message}");
			return;
		}

		// Seed + arm atomically on the UI thread: a switch (or re-arm against a different ref) that landed while the
		// git reads ran means this review is no longer active — drop it rather than seed onto the wrong session.
		_ui.Post(() => {
			if (!ReferenceEquals(ActiveReview(), review) || !IsActiveSession(session)) {
				return;
			}

			// Clear the web's stale markers (a re-arm over a prior review), then snap the tracker's board clean so a
			// file the session already changed that now equals the ref leaves the walk (it isn't in the ref diff, so
			// it wouldn't be re-seeded). Snapping commits any pending turn review — see docs/specs/diff-against.md.
			_bridge.PostToWeb(ChangeMessages.TurnReset());
			session.Changes.AcceptTurn();
			foreach (var (absolute, refContent, disk, existed) in seeds) {
				session.Changes.SeedRefBaseline(absolute, refContent, disk, existed);
			}

			PushTurnChangesToWeb();
			PushReviewHistoryToWeb();
			if (seeds.Count == 0) {
				return;
			}

			// A PR/ref review surfaces its code: open the first changed file at its first hunk + render it (comments
			// + diff). Post-turn review parks instead; the difference is the host-driven open here.
			string first = seeds[0].Absolute;
			int line = session.Changes.GetTurn(first) is { } turn ? LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText) ?? 1 : 1;
			session.FileOpener.Open(first, line, preview: true, scratch: false);
			PushReviewFileToWeb(first);
		});
	}

	/// <summary>The changed-file list for <paramref name="review"/> — the file axis of the diff walk.</summary>
	private static Task<IReadOnlyList<DiffFileChange>> ComputeReviewChangesAsync(DiffReview review) =>
		// A PR diffs merge-base → its committed head; a local "diff against" diffs merge-base → the working
		// tree, so uncommitted edits are part of the review (its per-file "current" is the disk file either way).
		review.PrNumber > 0
			? new GitService().DiffRefsAsync(review.Worktree, review.MergeBase, review.HeadRef, CancellationToken.None)
			: new GitService().DiffWorktreeAsync(review.Worktree, review.MergeBase, CancellationToken.None);

	/// <summary>Reads a worktree file's current content, treating a missing or unreadable file as empty (the current side of a diff).</summary>
	private static async Task<string> ReadWorktreeAsync(string absolutePath) {
		try {
			return File.Exists(absolutePath) ? await File.ReadAllTextAsync(absolutePath).ConfigureAwait(false) : string.Empty;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return string.Empty;
		}
	}

	/// <summary>
	/// Retracts the active review: commits the tracker's board so its seeded files leave the walk, then clears the
	/// web markers and pushes the (now empty) review set. Called when a re-diff finds nothing to review.
	/// </summary>
	private void RetractActiveReview(HostSession session) {
		_ui.Post(() => {
			// Bail if a new review armed between the caller's TryRemove and this post — else AcceptTurn() would
			// snap the freshly-seeded anchors, dropping the new review from the walk. ActiveReview() is null when
			// the removal still stands (nothing re-armed), which is exactly when the retract should proceed.
			if (!IsActiveSession(session) || ActiveReview() is not null) {
				return;
			}

			session.Changes.AcceptTurn();
			_bridge.PostToWeb(ChangeMessages.TurnReset());
			PushTurnChangesToWeb();
			PushReviewHistoryToWeb();
		});
	}

	/// <summary>
	/// Renders one review file: its comments (a PR only — a local ref has no forge behind it) then its inline diff,
	/// so the file shows with its Comment affordance + threads. Used at arm (the opened first file) and on each
	/// <c>get-turn-diff</c> step-in. On a plain turn (no active review) it's just the diff.
	/// </summary>
	private void PushReviewFileToWeb(string absolutePath) {
		if (ActiveReview() is { } review) {
			PushReviewCommentsToWeb(review, absolutePath); // self-guards: a no-op for a local ref (PrNumber 0)
		}

		PushTurnDiffToWeb(absolutePath);
	}

	/// <summary>
	/// Pushes one PR file's review comments (<c>review-comments</c>) so the inline diff anchors threads on it and
	/// shows the Comment button. A no-op for a local ref review (no forge, so no comments and no comment affordance).
	/// </summary>
	private void PushReviewCommentsToWeb(DiffReview review, string absolutePath) {
		if (review.PrNumber == 0) {
			return;
		}

		string relative = Path.GetRelativePath(review.Worktree, absolutePath).Replace('\\', '/');
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "review-comments",
			number = review.PrNumber,
			path = absolutePath,
			comments = review.Comments
				.Where(c => string.Equals(c.Path, relative, StringComparison.Ordinal))
				.Select(c => new {
					id = c.Id,
					line = c.Line,
					side = c.Side,
					author = c.Author,
					body = c.Body,
					createdAt = c.CreatedAt,
					inReplyTo = c.InReplyTo,
				}),
		}));
	}

	/// <summary>
	/// Re-surfaces the active session's review on a switch-in: a PR/ref review opens + renders its first changed
	/// file (a review shows its code), unlike a plain post-turn review which parks. Reads the persisted per-session
	/// tracker (no git), so it runs synchronously with the switch — no stale-diff race. A no-op when the incoming
	/// session has no armed review or its review has drained.
	/// </summary>
	private void SurfaceActiveReviewOnSwitch() {
		if (_session is not { } session || ActiveReview() is null) {
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

	/// <summary>The review armed for the active session, or <c>null</c> when it has none.</summary>
	private DiffReview? ActiveReview() =>
		_session is { } session && _diffReviews.TryGetValue(session.WorkspaceRoot, out var review) ? review : null;

	// A session's armed review: what seeding the tracker + posting comments needs — the merge-base to diff against
	// and the worktree it's checked out in. A pull request (PrNumber > 0, HeadRef the committed head, Repo the forge
	// repo, Comments loaded) or a local "diff against <ref>" (PrNumber 0, no forge). Label names it in the UI.
	private sealed record DiffReview(int PrNumber, string Label, string HeadRef, string MergeBase, string HeadSha, RepoRef? Repo, string Worktree) {
		/// <summary>The review's forge comments, refreshed on arm and after each post; empty for a local ref diff.</summary>
		public List<ReviewComment> Comments { get; } = [];
	}
}
