using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Review;

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
	/// Returns the first file to reveal; the page opens it only if the user hasn't navigated since asking.
	/// </summary>
	private async Task<ReviewReveal> DiffAgainstFromWebAsync(
		HostSession session,
		string reference,
		CancellationToken ct) {
		reference = reference.Trim();
		if (reference.Length == 0) {
			return ReviewReveal.None;
		}

		object request = session.Changes.BeginReviewRequest();
		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		ReviewContext review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, ct).ConfigureAwait(false) is not { } target) {
				Notify(session, "warn", $"'{reference}' isn't a branch, tag, or commit here.");
				return ReviewReveal.None;
			}

			string head = await git.GetHeadCommitAsync(worktree, ct).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, ct).ConfigureAwait(false) is not { } mergeBase) {
				Notify(session, "warn", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return ReviewReveal.None;
			}

			review = new ReviewContext(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			if (session.Changes.Review is { } existing && existing.SameSource(review))
				review = review with { MergeBase = existing.MergeBase };
			changes = await ComputeReviewChangesAsync(review, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return ReviewReveal.None;
		}

		if (changes.Count == 0) {
			// An empty Git diff does not discard saved rejection receipts.
			Notify(session, "info", $"No changes against '{reference}'.");
			if (session.Changes.Review is null) return ReviewReveal.None;
		}

		try {
			return await SeedAndArmReviewAsync(review, session, changes, request, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or InvalidOperationException) {
			Notify(session, "warn", $"Couldn't open the review: {ex.Message}");
			return ReviewReveal.None;
		}
	}

	/// <summary>
	/// Seeds the session's change tracker from <paramref name="review"/>'s base→current diff, so the review (a PR
	/// or a local ref) runs through the same inline accept/reject engine as a turn: each file's baseline is its
	/// content at the merge-base, its current the worktree file. Records the review, pushes the review set + the
	/// first file's diff, and returns that file for the caller to reveal (a review surfaces its code — post-turn
	/// review parks). Later hunk steps render lazily via <c>get-turn-diff</c>. A diff read failing toasts, leaving
	/// the session usable.
	/// </summary>
	private async Task<ReviewReveal> SeedAndArmReviewAsync(
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
		return await _ui.InvokeAsync(() => {
			string[] priorPaths = [.. session.Changes.TurnChanges().Select(change => change.Path)];
			if (!session.Changes.ArmReview(review, seeds.Select(seed => new ReviewSeed(seed.Absolute,
				seed.Baseline.Content, seed.Current.Content, seed.Baseline.Exists, seed.Current.Exists)).ToArray(), request))
				return Task.FromResult(ReviewReveal.None);

			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
			foreach (string path in priorPaths.Union(session.Changes.TurnChanges().Select(change => change.Path)))
				PushTurnDiffToWeb(session, path);
			if (resuming || seeds.Count == 0) {
				return Task.FromResult(ReviewReveal.None);
			}

			var firstSeed = seeds.FirstOrDefault(seed => seed.Current.Exists);
			if (firstSeed == default) {
				return Task.FromResult(ReviewReveal.None);
			}

			string first = firstSeed.Absolute;
			int? line = session.Changes.GetTurn(first) is { } turn
				? LineDiff.FirstChangedLine(turn.BaselineText, turn.CurrentText)
				: null;
			PushTurnDiffToWeb(session, first);
			return Task.FromResult(new ReviewReveal(first, line));
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

	private static ReviewContext? ActiveReview(HostSession session) => session.Changes.Review;
}

/// <summary>The file a freshly armed review surfaces, or <see cref="None"/> when there is nothing to reveal.</summary>
internal sealed record ReviewReveal(string? Path, int? Line) {
	public static ReviewReveal None { get; } = new(null, null);
}
