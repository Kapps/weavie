using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The commits a branch review covers (its merge-base to HEAD), for the session overview. Refreshed when HEAD moves
// and when the session's review changes source; a review that isn't against a branch has none.
public sealed partial class HostCore {
	private CoalescedSnapshotMonitor<ReviewCommitsSnapshot> AttachReviewCommits(HostSession session) {
		var monitor = new CoalescedSnapshotMonitor<ReviewCommitsSnapshot>(
			session.Background,
			ct => ResolveReviewCommitsAsync(ActiveReview(session), ct),
			commits => session.Bus.BroadcastTarget.Feature("review").Publish("commits", WireJson.Default.ReviewCommitsSnapshot, commits));
		session.AttachReviewCommits(monitor);
		monitor.RequestRefresh();
		return monitor;
	}

	private static void PushReviewCommits(HostSession session, MessageTarget target) {
		if (session.ReviewCommits.Latest is { } latest) {
			target.Feature("review").Publish("commits", WireJson.Default.ReviewCommitsSnapshot, latest);
		}
	}

	private static async Task<ReviewCommitsSnapshot> ResolveReviewCommitsAsync(ReviewContext? review, CancellationToken ct) {
		if (review is null) {
			return ReviewCommitsSnapshot.None;
		}

		try {
			return new(review.MergeBase, await new GitService().LogRangeAsync(review.Worktree, review.MergeBase, "HEAD", ct).ConfigureAwait(false), null);
		} catch (GitException ex) {
			return new(review.MergeBase, [], ex.Message);
		}
	}
}

/// <summary>A branch review's commits, newest first; <see cref="Base"/> is null when the review isn't against a branch.</summary>
internal sealed record ReviewCommitsSnapshot(string? Base, IReadOnlyList<GitCommit> Commits, string? Error) {
	public static ReviewCommitsSnapshot None { get; } = new(null, [], null);

	public bool Equals(ReviewCommitsSnapshot? other) =>
		other is not null && Base == other.Base && Error == other.Error && Commits.SequenceEqual(other.Commits);

	public override int GetHashCode() => HashCode.Combine(Base, Error, Commits.Count);
}
