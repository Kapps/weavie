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

		object request = session.Changes.BeginReviewRequest();
		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		ReviewContext review;
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

			review = new ReviewContext(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			if (session.Changes.Review is { } existing && existing.SameSource(review))
				review = review with { MergeBase = existing.MergeBase };
			changes = await ComputeReviewChangesAsync(review, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return;
		}

		if (changes.Count == 0) {
			// An empty Git diff does not discard saved rejection receipts.
			Notify(session, "info", $"No changes against '{reference}'.");
			if (session.Changes.Review is null) return;
		}

		try {
			await SeedAndArmReviewAsync(review, session, changes, request, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or InvalidOperationException) {
			Notify(session, "warn", $"Couldn't open the review: {ex.Message}");
		}
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
		ReviewContext review,
		HostSession session,
		IReadOnlyList<DiffFileChange> changes,
		object request,
		CancellationToken ct) {
		bool resuming = session.Changes.Review is not null;

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
			throw;
		}

		// Seed + arm atomically: a newer review may replace this one while its git reads are running.
		await _ui.InvokeAsync(() => {
			string[] priorPaths = [.. session.Changes.TurnChanges().Select(change => change.Path)];
			if (!session.Changes.ArmReview(review, seeds.Select(seed => new ReviewSeed(seed.Absolute,
				seed.Baseline.Content, seed.Current.Content, seed.Baseline.Exists, seed.Current.Exists)).ToArray(), request))
				return Task.CompletedTask;

			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
			foreach (string path in priorPaths.Union(session.Changes.TurnChanges().Select(change => change.Path)))
				PushReviewFileToWeb(session, path);
			if (resuming || seeds.Count == 0) {
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
		ReviewContext review,
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
		ReviewContext review,
		string absolutePath) =>
		PushReviewCommentsToWeb(
			review,
			absolutePath,
			session.Bus.BroadcastTarget);

	private static void PushReviewCommentsToWeb(
		ReviewContext review,
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

	private static ReviewContext? ActiveReview(HostSession session) => session.Changes.Review;
}
