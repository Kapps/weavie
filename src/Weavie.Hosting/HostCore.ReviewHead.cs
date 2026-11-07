using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// Keeps the open review on the branch it was opened for: another branch checked out closes it, and a merge or
// rebase that moves the merge-base with its target moves the review there. A rebase under way detaches HEAD, so
// the review waits for it to finish. See docs/specs/diff-against.md.
public sealed partial class HostCore {
	private CoalescedSnapshotMonitor<ReviewHeadSnapshot> AttachReviewHead(HostSession session) {
		var monitor = new CoalescedSnapshotMonitor<ReviewHeadSnapshot>(
			session.Background,
			ct => ResolveReviewHeadAsync(session, ct),
			head => _ = session.Background.Run(ct => FollowReviewHeadAsync(session, head, ct)));
		session.Changes.ReviewChanged += monitor.RefreshNow;
		session.Changes.ReviewFilesRestarted += paths => PostForSession(session, () => Notify(session, "info",
			$"Your kept changes overlapped the review's new base, so these start over: "
			+ string.Join(", ", paths.Select(path => Path.GetRelativePath(session.WorkspaceRoot, path))) + "."));
		monitor.RequestRefresh();
		return monitor;
	}

	private static async Task<ReviewHeadSnapshot> ResolveReviewHeadAsync(HostSession session, CancellationToken ct) {
		if (ActiveReview(session) is not { } review) return ReviewHeadSnapshot.None;
		var git = new GitService();
		try {
			if (await git.IsRebasingAsync(review.Worktree, ct).ConfigureAwait(false)) return ReviewHeadSnapshot.None;
			string branch = await git.GetCurrentBranchAsync(review.Worktree, ct).ConfigureAwait(false) ?? string.Empty;
			string? mergeBase = branch != review.Branch ? null
				: await git.MergeBaseAsync(review.Worktree, review.Target, "HEAD", ct).ConfigureAwait(false);
			return new(review, branch, mergeBase);
		} catch (GitException ex) {
			Log($"[weavie] review '{review.Label}': couldn't read its branch: {ex.Message}");
			return ReviewHeadSnapshot.None;
		}
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

	/// <summary>The open review and the branch and merge-base it would have if opened now.</summary>
	private sealed record ReviewHeadSnapshot(ReviewContext? Review, string Branch, string? MergeBase) {
		public static ReviewHeadSnapshot None { get; } = new(null, string.Empty, null);
	}
}
