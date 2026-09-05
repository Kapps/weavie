using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The review-diff surface: a session's worktree diffed against a base commit, reviewed through the SAME inline
// accept/reject engine as a turn (HostCore.WebBridge.cs). Fed by two producers — an opened pull request
// (HostCore.PullRequests.cs) and the local "diff against <ref>" command — which both SEED the session's change
// tracker from the merge-base and let keep/revert + accumulating new-turn edits flow through the shared
// turn-changes / turn-diff messages. See docs/specs/diff-against.md.
public sealed partial class HostCore {
	// Each worktree's armed review survives unloading/reloading its live session. Arming another replaces it.
	private readonly ConcurrentDictionary<string, DiffReview> _diffReviews = new(StringComparer.Ordinal);

	/// <summary>
	/// Arms a "diff against &lt;ref&gt;" review on its owning session: resolves the ref to a commit, diffs the
	/// working tree from its merge-base with HEAD (so a branch shows only this side's changes), and seeds the
	/// change tracker so the diff reviews through the same accept/reject engine as a turn. Failures surface as
	/// toasts; an empty diff says so and retracts any prior review instead of arming an unwalkable navigator.
	/// </summary>
	private async Task DiffAgainstFromWebAsync(
		HostSession session,
		string reference,
		CancellationToken ct) {
		reference = reference.Trim();
		if (reference.Length == 0) {
			return;
		}

		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		DiffReview review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, ct).ConfigureAwait(false) is not { } target) {
				Notify(session, "warn", $"'{reference}' isn't a branch, tag, or commit here.");
				return;
			}

			string head = await git.GetHeadCommitAsync(worktree, ct).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, ct).ConfigureAwait(false) is not { } mergeBase) {
				Notify(session, "warn", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return;
			}

			review = new DiffReview(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			changes = await ComputeReviewChangesAsync(review, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return;
		}

		if (changes.Count == 0) {
			// Nothing to review: answer where the user is (a toast), and retract any prior review so a stale walk
			// can't sit under the "no changes" answer. Retracting commits the tracker's board — but an empty diff
			// means the worktree equals the ref, so there are no pending edits to lose.
			Notify(session, "info", $"No changes against '{reference}'.");
			if (_diffReviews.TryRemove(worktree, out _)) {
				RetractActiveReview(session);
			}

			return;
		}

		await SeedAndArmReviewAsync(review, session, changes, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Seeds the session's change tracker from <paramref name="review"/>'s base→current diff, so the review (a PR
	/// or a local ref) runs through the same inline accept/reject engine as a turn: each file's baseline is its
	/// content at the merge-base, its current the worktree file. Records the review, pushes the review set + the
	/// first file's diff (+ comments for a PR), and opens that file (a review surfaces its code — post-turn review
	/// parks). Later hunk steps render lazily via <c>get-turn-diff</c>. A diff read failing toasts, leaving the
	/// session usable.
	/// </summary>
	private async Task SeedAndArmReviewAsync(
		DiffReview review,
		HostSession session,
		IReadOnlyList<DiffFileChange> changes,
		CancellationToken ct) {
		// Record the review up front so a rapid re-arm (a second diff-against / PR open) replaces it here; the
		// guarded post below then sees a different ActiveReview() and bails, so a stale arm can't seed onto the
		// now-active review.
		_diffReviews[review.Worktree] = review;

		var git = new GitService();
		var seeds = new List<(string Absolute, GitFileSnapshot Baseline, WorktreeFileSnapshot Current)>();
		try {
			foreach (var change in changes) {
				string absolute = Path.GetFullPath(Path.Combine(review.Worktree, change.Path));
				var baseline = await git
					.ReadFileAtRefAsync(review.Worktree, review.MergeBase, change.Path, ct)
					.ConfigureAwait(false);
				var current = await ReadWorktreeAsync(absolute, ct).ConfigureAwait(false);
				seeds.Add((absolute, baseline, current));
			}
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			Log($"[weavie] review '{review.Label}': diff failed: {ex.Message}");
			Notify(session, "warn", $"Armed the review, but couldn't compute its diff: {ex.Message}");
			return;
		}

		// Seed + arm atomically: a newer review may replace this one while its git reads are running.
		await _ui.InvokeAsync(() => {
			if (!ReferenceEquals(ActiveReview(session), review)) {
				return Task.CompletedTask;
			}

			// Snap the tracker's board clean so a file the session already changed that now equals the ref leaves the
			// walk (it isn't in the ref diff, so
			// it wouldn't be re-seeded). Snapping commits any pending turn review — see docs/specs/diff-against.md.
			session.Changes.AcceptTurn();
			foreach (var (absolute, baseline, current) in seeds) {
				session.Changes.SeedRefBaseline(
					absolute,
					baseline.Content,
					current.Content,
					baseline.Exists,
					current.Exists);
			}

			session.Bus.Feature("review").PublishJson("reset", ChangeMessages.TurnReset());
			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
			if (seeds.Count == 0) {
				return Task.CompletedTask;
			}

			var firstSeed = seeds.FirstOrDefault(seed => seed.Current.Exists);
			if (firstSeed == default) {
				return Task.CompletedTask;
			}

			string first = firstSeed.Absolute;
			int? line = session.Changes.GetTurn(first) is { } turn
				? LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText)
				: null;
			session.FileOpener.Open(first, line, preview: true, scratch: false, EditorOpenIntent.Reveal);
			PushReviewFileToWeb(session, first);
			return Task.CompletedTask;
		}, ct).ConfigureAwait(false);
	}

	/// <summary>The changed-file list for <paramref name="review"/> — the file axis of the diff walk.</summary>
	private static Task<IReadOnlyList<DiffFileChange>> ComputeReviewChangesAsync(
		DiffReview review,
		CancellationToken ct) =>
		// A PR diffs merge-base → its committed head; a local "diff against" diffs merge-base → the working
		// tree, so uncommitted edits are part of the review (its per-file "current" is the disk file either way).
		review.PrNumber > 0
			? new GitService().DiffRefsAsync(review.Worktree, review.MergeBase, review.HeadRef, ct)
			: new GitService().DiffWorktreeAsync(review.Worktree, review.MergeBase, ct);

	/// <summary>Reads a worktree file's current content while preserving absence as review data.</summary>
	private static async Task<WorktreeFileSnapshot> ReadWorktreeAsync(string absolutePath, CancellationToken ct) =>
		File.Exists(absolutePath)
			? new WorktreeFileSnapshot(true, await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false))
			: new WorktreeFileSnapshot(false, string.Empty);

	private readonly record struct WorktreeFileSnapshot(bool Exists, string Content);

	/// <summary>
	/// Retracts the active review: commits the tracker's board so its seeded files leave the walk, then clears the
	/// web markers and pushes the (now empty) review set. Called when a re-diff finds nothing to review.
	/// </summary>
	private void RetractActiveReview(HostSession session) {
		PostForSession(session, () => {
			// Bail if a new review armed between the caller's TryRemove and this post — else AcceptTurn() would
			// snap the freshly-seeded anchors, dropping the new review from the walk. ActiveReview() is null when
			// the removal still stands (nothing re-armed), which is exactly when the retract should proceed.
			if (ActiveReview(session) is not null) {
				return;
			}

			session.Changes.AcceptTurn();
			session.Bus.Feature("review").PublishJson("reset", ChangeMessages.TurnReset());
			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
		});
	}

	/// <summary>
	/// Renders one review file: its comments (a PR only — a local ref has no forge behind it) then its inline diff,
	/// so the file shows with its Comment affordance + threads. Used at arm (the opened first file) and on each
	/// <c>get-turn-diff</c> step-in. On a plain turn (no active review) it's just the diff.
	/// </summary>
	private void PushReviewFileToWeb(HostSession session, string absolutePath) =>
		PushReviewFileToWeb(session, absolutePath, session.Bus.BroadcastTarget);

	private void PushReviewFileToWeb(
		HostSession session,
		string absolutePath,
		MessageTarget target) {
		if (ActiveReview(session) is { } review) {
			PushReviewCommentsToWeb(review, absolutePath, target);
		}

		PushTurnDiffToWeb(session, absolutePath, target);
	}

	/// <summary>
	/// Pushes one PR file's review comments (<c>review-comments</c>) so the inline diff anchors threads on it and
	/// shows the Comment button. A no-op for a local ref review (no forge, so no comments and no comment affordance).
	/// </summary>
	private static void PushReviewCommentsToWeb(
		HostSession session,
		DiffReview review,
		string absolutePath) =>
		PushReviewCommentsToWeb(
			review,
			absolutePath,
			session.Bus.BroadcastTarget);

	private static void PushReviewCommentsToWeb(
		DiffReview review,
		string absolutePath,
		MessageTarget target) {
		if (review.PrNumber == 0) {
			return;
		}

		string relative = Path.GetRelativePath(review.Worktree, absolutePath).Replace('\\', '/');
		target.Feature("review").Publish("comments", new {
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
		});
	}

	private DiffReview? ActiveReview(HostSession session) =>
		_diffReviews.TryGetValue(session.WorkspaceRoot, out var review) ? review : null;

	// A session's armed review: what seeding the tracker + posting comments needs — the merge-base to diff against
	// and the worktree it's checked out in. A pull request (PrNumber > 0, HeadRef the committed head, Repo the forge
	// repo, Comments loaded) or a local "diff against <ref>" (PrNumber 0, no forge). Label names it in the UI.
	private sealed record DiffReview(int PrNumber, string Label, string HeadRef, string MergeBase, string HeadSha, RepoRef? Repo, string Worktree) {
		/// <summary>The review's forge comments, refreshed on arm and after each post; empty for a local ref diff.</summary>
		public List<ReviewComment> Comments { get; } = [];
	}
}
