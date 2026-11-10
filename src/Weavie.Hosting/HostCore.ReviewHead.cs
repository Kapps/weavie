using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// Keeps the open review on the branch it was opened for: another branch checked out closes it, and a merge or
// rebase that moves the merge-base with its target moves the review there. A rebase or bisect under way detaches
// HEAD, so the review waits for it to finish. See docs/specs/diff-against.md.
public sealed partial class HostCore {
	private CoalescedSnapshotMonitor<ReviewHeadSnapshot> AttachReviewHead(HostSession session) {
		var monitor = new CoalescedSnapshotMonitor<ReviewHeadSnapshot>(
			session.Background,
			ct => ResolveReviewHeadAsync(session, ct),
			head => _ = session.Background.Run(ct => FollowReviewHeadAsync(session, head, ct)));
		session.AttachReviewHead(monitor);
		session.Changes.ReviewChanged += monitor.RefreshNow;
		session.Changes.ReviewFilesRestarted += paths => PostForSession(session, () => Notify(session, "info",
			$"Your kept changes overlapped the review's base, so these start over against it: "
			+ string.Join(", ", paths.Select(path => Path.GetRelativePath(session.WorkspaceRoot, path))) + "."));
		monitor.RequestRefresh();
		return monitor;
	}

	private static async Task<ReviewHeadSnapshot> ResolveReviewHeadAsync(HostSession session, CancellationToken ct) {
		if (ActiveReview(session) is not { } review) return ReviewHeadSnapshot.None;
		var git = new GitService();
		try {
			return await ReadReviewHeadAsync(
				review,
				token => git.GetCurrentBranchAsync(review.Worktree, token),
				token => git.IsRebasingOrBisectingAsync(review.Worktree, token),
				token => git.MergeBaseAsync(review.Worktree, review.Target, "HEAD", token),
				ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Log($"[weavie] review '{review.Label}': couldn't read its branch: {ex.Message}");
			return ReviewHeadSnapshot.None;
		}
	}

	internal static async Task<ReviewHeadSnapshot> ReadReviewHeadAsync(
		ReviewContext review,
		Func<CancellationToken, Task<string?>> readBranch,
		Func<CancellationToken, Task<bool>> operationInProgress,
		Func<CancellationToken, Task<string?>> readMergeBase,
		CancellationToken ct) {
		string branch = await readBranch(ct).ConfigureAwait(false) ?? string.Empty;
		if (branch != review.Branch) {
			if (await operationInProgress(ct).ConfigureAwait(false)) return ReviewHeadSnapshot.Waiting;
			// A finishing rebase restores its branch before removing its marker; use that restored identity.
			branch = await readBranch(ct).ConfigureAwait(false) ?? string.Empty;
		}

		return new(review, branch, branch == review.Branch
			? await readMergeBase(ct).ConfigureAwait(false)
			: null);
	}

	private async Task FollowReviewHeadAsync(HostSession session, ReviewHeadSnapshot head, CancellationToken ct) {
		if (head.Review is not { } review) return;
		if (head.Branch != review.Branch) {
			PostForSession(session, () => {
				if (!session.Changes.RetireReview(review)) return;
				PushReviewActionToWeb(session, []);
				Notify(session, "info", head.Branch.Length == 0
					? $"Closed the review {review.Label}: HEAD left '{review.Branch}'."
					: $"Closed the review {review.Label}: the worktree is now on '{head.Branch}'.");
			});
			return;
		}

		if (head.MergeBase is not { } mergeBase || mergeBase == review.MergeBase) return;
		var moved = review with { MergeBase = mergeBase };
		try {
			await SeedAndArmReviewAsync(
				moved, session, await ComputeReviewChangesAsync(moved, ct).ConfigureAwait(false),
				seeds => session.Changes.RebaseReview(review, moved, seeds), ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException or InvalidOperationException) {
			Notify(session, "warn", $"Couldn't move the review {review.Label} to its new merge-base: {ex.Message}");
		}
	}

}

/// <summary>The open review and the branch and merge-base it would have if opened now.</summary>
internal sealed record ReviewHeadSnapshot(ReviewContext? Review, string Branch, string? MergeBase) {
	public static ReviewHeadSnapshot None { get; } = new(null, string.Empty, null);

	/// <summary>A rebase or bisect has HEAD off the branch for now; the review neither closes nor moves.</summary>
	public static ReviewHeadSnapshot Waiting { get; } = new(null, "(waiting)", null);
}
