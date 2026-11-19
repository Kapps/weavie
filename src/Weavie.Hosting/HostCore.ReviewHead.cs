using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// Keeps the open git review on its branch and current with git: another branch checked out closes it, a moved
// merge-base re-lists it, and files the watcher saw change join it at its base. A git operation under way (a rebase,
// bisect, stopped merge, or anything holding the index lock) makes it wait. See docs/specs/review-state.md.
public sealed partial class HostCore {
	// A shell edit shows within this; a burst of them re-lists at most this often.
	private static readonly TimeSpan ReviewRelistInterval = TimeSpan.FromSeconds(1);

	private CoalescedSnapshotMonitor<ReviewHeadSnapshot> AttachReviewHead(HostSession session) {
		var monitor = new CoalescedSnapshotMonitor<ReviewHeadSnapshot>(
			session.Background,
			ct => ResolveReviewHeadAsync(session, ct),
			head => _ = session.Background.Run(ct => FollowReviewHeadAsync(session, head, ct)),
			Task.Delay,
			ReviewRelistInterval);
		session.AttachReviewHead(monitor);
		session.Changes.ReviewChanged += monitor.RefreshNow;
		monitor.RequestRefresh();
		return monitor;
	}

	private async Task<ReviewHeadSnapshot> ResolveReviewHeadAsync(HostSession session, CancellationToken ct) {
		if (ActiveReview(session) is not { } review) return ReviewHeadSnapshot.None;
		var git = new GitService();
		try {
			return await ReadReviewHeadAsync(
				review,
				session.ReviewListing.Observe(),
				token => git.GetCurrentBranchAsync(review.Worktree, token),
				token => git.ReadOperationAsync(review.Worktree, token),
				// No merge-base closes the review only when its target is gone; any other failure keeps it.
				async token => await git.MergeBaseAsync(review.Worktree, review.Target, "HEAD", token).ConfigureAwait(false) is { } mergeBase
					? mergeBase
					: await git.ResolveCommitAsync(review.Worktree, review.Target, token).ConfigureAwait(false) is null
						? null
						: throw new GitException($"'{review.Target}' has no merge-base with HEAD."),
				ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't follow the review {review.Label}: {ex.Message}");
			return ReviewHeadSnapshot.None;
		}
	}

	internal static async Task<ReviewHeadSnapshot> ReadReviewHeadAsync(
		ReviewContext review,
		ListingState listing,
		Func<CancellationToken, Task<string?>> readBranch,
		Func<CancellationToken, Task<GitOperation>> readOperation,
		Func<CancellationToken, Task<string?>> readMergeBase,
		CancellationToken ct) {
		async Task<bool> Stopped() => await readOperation(ct).ConfigureAwait(false) != GitOperation.None;

		// The merge-base is read first: HEAD only moves forward, so a branch read after it can't predate it.
		string? mergeBase = await readMergeBase(ct).ConfigureAwait(false);
		string branch = await readBranch(ct).ConfigureAwait(false) ?? string.Empty;
		if (branch != review.Branch) {
			if (await Stopped().ConfigureAwait(false)) return ReviewHeadSnapshot.Waiting;
			// A finishing rebase restores its branch before removing its marker; use that restored identity.
			branch = await readBranch(ct).ConfigureAwait(false) ?? string.Empty;
			if (branch != review.Branch) return new(review, branch, null, listing);
			mergeBase = await readMergeBase(ct).ConfigureAwait(false);
		}

		// A re-list reads the working tree, so it waits out an operation still rewriting it.
		if ((listing.Stale || mergeBase != review.MergeBase) && await Stopped().ConfigureAwait(false))
			return ReviewHeadSnapshot.Waiting;
		return new(review, branch, mergeBase, listing);
	}

	// Joins the files pending when the base was checked, unless HEAD moved since: then the base is checked again first.
	private async Task JoinPendingAsync(HostSession session, ReviewContext review, ListingState seen, CancellationToken ct) {
		if (session.ReviewListing.HeadMovedSince(seen)) {
			session.ReviewHead.RefreshNow();
			return;
		}
		var paths = session.ReviewListing.PendingAsOf(seen);
		try {
			await JoinAndRefreshReviewAsync(session, paths, ct).ConfigureAwait(false);
			session.ReviewListing.Joined(paths, seen);
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			// A full listing picks up what couldn't join; it settles even when it fails too, so this never loops.
			session.ReviewListing.RequireFull();
			session.ReviewHead.RequestRefresh();
			Notify(session, "warn", $"Couldn't refresh the review {review.Label}: {ex.Message}");
		}
	}

	private async Task FollowReviewHeadAsync(HostSession session, ReviewHeadSnapshot head, CancellationToken ct) {
		if (head.Review is not { } review) return;
		if (head.Branch != review.Branch || head.MergeBase is null) {
			PostForSession(session, () => {
				if (!session.Changes.RetireReview(review)) return;
				PushReviewActionToWeb(session, []);
				Notify(session, "info", head.Branch != review.Branch
					? head.Branch.Length == 0
						? $"Closed the review {review.Label}: HEAD left '{review.Branch}'."
						: $"Closed the review {review.Label}: the worktree is now on '{head.Branch}'."
					: $"Closed the review {review.Label}: '{review.Target}' no longer exists.");
			});
			return;
		}

		string mergeBase = head.MergeBase;
		// With its base where it was, the review only needs the files the watcher saw change.
		if (mergeBase == review.MergeBase && !head.Listing.Full) {
			if (head.Listing.Pending) await JoinPendingAsync(session, review, head.Listing, ct).ConfigureAwait(false);
			return;
		}

		if (session.Changes.BeginRelist(review) is not { } request) return;
		try {
			// The snapshot's listing state was observed before its merge-base was read, so a HEAD move since voids both.
			if (await ListReviewAsync(session, review with { MergeBase = mergeBase }, request, head.Listing, ct).ConfigureAwait(false)
				== Listed.HeadMoved) session.ReviewHead.RefreshNow();
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't refresh the review {review.Label}: {ex.Message}");
		} finally {
			EndListing(session, request);
		}
	}
}

/// <summary>
/// The open review, the branch and merge-base it would have if opened now, and its listing state observed before
/// either was read (so any change to it, including a finished listing, re-evaluates the review).
/// </summary>
internal sealed record ReviewHeadSnapshot(ReviewContext? Review, string Branch, string? MergeBase, ListingState Listing) {
	public static ReviewHeadSnapshot None { get; } = new(null, string.Empty, null, default);

	/// <summary>A git operation is under way; the review neither closes nor re-lists until it finishes.</summary>
	public static ReviewHeadSnapshot Waiting { get; } = new(null, "(waiting)", null, default);
}
