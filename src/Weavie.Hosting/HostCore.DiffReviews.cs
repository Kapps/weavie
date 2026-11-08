using System.Text.Json.Serialization;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
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
	/// Answers whether a new review opened; the page shows its overview only if the user hasn't navigated since asking.
	/// A review against another source is replaced only when <paramref name="replaces"/> names it, the review the user
	/// agreed to close; otherwise the answer names the open review so the page can ask (again).
	/// </summary>
	private async Task<ReviewOpened> DiffAgainstFromWebAsync(
		HostSession session,
		string reference,
		string? replaces,
		CancellationToken ct) {
		reference = reference.Trim();
		if (reference.Length == 0) {
			return ReviewOpened.No;
		}

		object request = session.Changes.BeginReviewRequest();
		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		ReviewContext review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, ct).ConfigureAwait(false) is not { } target) {
				Notify(session, "warn", $"'{reference}' isn't a branch, tag, or commit here.");
				return ReviewOpened.No;
			}

			string head = await git.GetHeadCommitAsync(worktree, ct).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, ct).ConfigureAwait(false) is not { } mergeBase) {
				Notify(session, "warn", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return ReviewOpened.No;
			}

			string branch = await git.GetCurrentBranchAsync(worktree, ct).ConfigureAwait(false) ?? string.Empty;
			review = new ReviewContext(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree, reference, branch);
			changes = await ComputeReviewChangesAsync(review, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return ReviewOpened.No;
		}

		if (changes.Count == 0) {
			// An empty Git diff does not discard saved rejection receipts.
			Notify(session, "info", $"No changes against '{reference}'.");
			if (session.Changes.Review is not { } open || !open.SameSource(review)) return ReviewOpened.No;
		}

		if (session.Changes.Review is { } current && !current.SameSource(review) && current.Label != replaces) {
			return new(ReviewOutcome.None, current.Label, review.Label);
		}

		try {
			var outcome = await SeedAndArmReviewAsync(review, session, changes, seeds => replaces is null
				? session.Changes.ArmReview(review, seeds, request)
				: session.Changes.ReplaceReview(review, seeds, request, replaces), ct).ConfigureAwait(false);
			// Another review took over while this one was computing: ask about that one instead.
			return outcome == ReviewOutcome.None && replaces is not null && session.Changes.Review is { } now && !now.SameSource(review)
				? new(ReviewOutcome.None, now.Label, review.Label)
				: new(outcome, null, review.Label);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or InvalidOperationException) {
			Notify(session, "warn", $"Couldn't open the review: {ex.Message}");
			return ReviewOpened.No;
		}
	}

	/// <summary>
	/// Diffs against a ref the user named; once that review is open, a branch becomes the branch's review base (see
	/// <see cref="GitService.ChooseBranchBaseAsync"/>), so Review Branch keeps comparing with it. That runs after the
	/// answer: the page reveals the review only if the user hasn't navigated, so a late answer would override them.
	/// </summary>
	private async Task<ReviewOpened> ChooseDiffAgainstFromWebAsync(
		HostSession session,
		string reference,
		string? replaces,
		CancellationToken ct) {
		var result = await DiffAgainstFromWebAsync(session, reference, replaces, ct).ConfigureAwait(false);
		if (result.Replaces is null && session.Changes.Review?.Label == result.Label) {
			_ = session.Background.Run(stopping => ChooseBranchBaseAsync(session, reference.Trim(), stopping));
		}

		return result;
	}

	private async Task ChooseBranchBaseAsync(HostSession session, string chosen, CancellationToken ct) {
		try {
			if (await DefaultBranchRefAsync(session.WorkspaceRoot, ct).ConfigureAwait(false) is { } preferred
				&& await new GitService().ChooseBranchBaseAsync(
					session.WorkspaceRoot, chosen, preferred, PullRequestBase(session), ct).ConfigureAwait(false)) {
				Notify(session, "info", $"Review Branch now compares with '{chosen}'.");
			}
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't remember '{chosen}' as this branch's review base: {ex.Message}");
		}
	}

	/// <summary>Reviews the whole branch: a diff against <see cref="BranchBaseRefAsync"/> from their merge-base.</summary>
	private async Task<ReviewOpened> DiffAgainstBaseFromWebAsync(HostSession session, string? replaces, CancellationToken ct) {
		if (await BranchBaseRefAsync(session, ct).ConfigureAwait(false) is not { } reference) {
			Notify(session, "warn", "This repository has no default branch (origin/HEAD, main, or master) to review against.");
			return ReviewOpened.No;
		}

		return await DiffAgainstFromWebAsync(session, reference, replaces, ct).ConfigureAwait(false);
	}

	/// <summary>The ref a whole-branch review compares against (see <see cref="GitService.ResolveBranchBaseAsync"/>).</summary>
	private static async Task<string?> BranchBaseRefAsync(HostSession session, CancellationToken ct) =>
		await DefaultBranchRefAsync(session.WorkspaceRoot, ct).ConfigureAwait(false) is { } preferred
			? await new GitService().ResolveBranchBaseAsync(
				session.WorkspaceRoot, preferred, PullRequestBase(session), ct).ConfigureAwait(false)
			: null;

	/// <summary>
	/// The remote's default branch, else the local one. The remote's copy wins because a local default branch is often
	/// far behind and would fold others' merged work into the diff.
	/// </summary>
	private static async Task<string?> DefaultBranchRefAsync(string worktree, CancellationToken ct) {
		var git = new GitService();
		if (await git.ResolveDefaultBranchAsync(worktree, ct).ConfigureAwait(false) is not { } branch) {
			return null;
		}

		string remote = $"origin/{branch}";
		return await git.ResolveCommitAsync(worktree, remote, ct).ConfigureAwait(false) is null ? branch : remote;
	}

	/// <summary>The remote-tracking ref of the branch the session's open pull request targets, if it has one.</summary>
	private static string? PullRequestBase(HostSession session) =>
		session.PullRequestStatus.Latest is { PullRequest.State: "open", Target: { } target }
			? $"{target.Remote}/{target.BaseRef}"
			: null;

	/// <summary>
	/// Seeds the session's change tracker from <paramref name="review"/>'s base→current diff, so the review (a PR
	/// or a local ref) runs through the same inline accept/reject engine as a turn: each file's baseline is its
	/// content at the merge-base, its current the worktree file. Records the review, pushes the review set + the
	/// first file's diff, and answers whether it opened a new review or resumed the same source (keeping the user's place).
	/// <paramref name="arm"/> attaches the seeds on the UI thread, answering whether the review it was asked for armed.
	/// Later hunk steps render lazily via <c>get-turn-diff</c>. A diff read failing toasts, leaving the session usable.
	/// </summary>
	private async Task<ReviewOutcome> SeedAndArmReviewAsync(
		ReviewContext review,
		HostSession session,
		IReadOnlyList<DiffFileChange> changes,
		Func<ReviewSeed[], bool> arm,
		CancellationToken ct) {
		var git = new GitService();
		var existing = session.Changes.Review;
		bool resuming = existing is not null && existing.SameSource(review);
		var paths = changes.Select(change => change.Path).ToList();
		if (resuming && existing!.MergeBase != review.MergeBase) {
			// A review moving to a new merge-base re-reads every file it holds there that Git would show it.
			string root = Path.GetFullPath(review.Worktree);
			string[] held = [.. session.Changes.TrackedPaths().Where(path => path.StartsWith(root + Path.DirectorySeparatorChar, PathIdentity.Comparison))];
			var ignored = await git.FindIgnoredAsync(root, held, ct).ConfigureAwait(false);
			paths.AddRange(held.Where(path => !ignored.Contains(path))
				.Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
				.Except(paths, StringComparer.Ordinal));
		}

		var seeds = new List<(string Absolute, GitFileSnapshot Baseline, WorktreeFileSnapshot Current)>();
		try {
			foreach (string path in paths) {
				string absolute = Path.GetFullPath(Path.Combine(review.Worktree, path));
				var baseline = await git
					.ReadFileAtRefAsync(review.Worktree, review.MergeBase, path, ct)
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
			ReviewSeed[] armed = [.. seeds.Select(seed => new ReviewSeed(seed.Absolute,
				seed.Baseline.Content, seed.Current.Content, seed.Baseline.Exists, seed.Current.Exists))];
			bool armedReview;
			try {
				armedReview = arm(armed);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Arming may have replaced what the page holds revisions of before it failed.
				PushReviewStateToWeb(session, session.Bus.BroadcastTarget);
				throw;
			}

			if (!armedReview)
				return Task.FromResult(ReviewOutcome.None);

			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
			foreach (string path in priorPaths.Union(session.Changes.TurnChanges().Select(change => change.Path)))
				PushTurnDiffToWeb(session, path);
			return Task.FromResult(resuming ? ReviewOutcome.Resumed : seeds.Count > 0 ? ReviewOutcome.Opened : ReviewOutcome.None);
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

/// <summary>
/// What a request did with its review <c>Label</c>; <c>Replaces</c> names the review it would close when it was
/// refused for it.
/// </summary>
internal sealed record ReviewOpened(ReviewOutcome Outcome, string? Replaces, string Label) {
	public static ReviewOpened No { get; } = new(ReviewOutcome.None, null, string.Empty);
}

/// <summary>Whether a review request armed a new review, resumed the one already open, or showed nothing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewOutcome>))]
internal enum ReviewOutcome {
	[JsonStringEnumMemberName("none")]
	None,

	[JsonStringEnumMemberName("opened")]
	Opened,

	[JsonStringEnumMemberName("resumed")]
	Resumed,
}
