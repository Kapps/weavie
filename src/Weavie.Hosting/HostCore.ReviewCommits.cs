using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The branch's commits for the session overview, each marked by whether the session's review spans it. Refreshed when
// HEAD moves and, without the burst cooldown, when the review changes source.
public sealed partial class HostCore {
	private CoalescedSnapshotMonitor<ReviewCommitsSnapshot> AttachReviewCommits(HostSession session) {
		var monitor = new CoalescedSnapshotMonitor<ReviewCommitsSnapshot>(
			session.Background,
			ct => ResolveReviewCommitsAsync(session.WorkspaceRoot, ActiveReview(session), ct),
			commits => session.Bus.BroadcastTarget.Feature("review").Publish("commits", WireJson.Default.ReviewCommitsSnapshot, commits));
		session.AttachReviewCommits(monitor);
		session.Changes.ReviewChanged += monitor.RefreshNow;
		monitor.RequestRefresh();
		return monitor;
	}

	private static void PushReviewCommits(HostSession session, MessageTarget target) {
		if (session.ReviewCommits.Latest is { } latest) {
			target.Feature("review").Publish("commits", WireJson.Default.ReviewCommitsSnapshot, latest);
		}
	}

	// The branch starts at its PR's merge-base, else where it left the default branch (or, with none, at the review's
	// base); the review spans the commits after its own base. Range logs over the branch only, never a history walk.
	private static async Task<ReviewCommitsSnapshot> ResolveReviewCommitsAsync(string worktree, ReviewContext? review, CancellationToken ct) {
		var git = new GitService();
		try {
			string? defaultRef = await DefaultDiffRefAsync(worktree, ct).ConfigureAwait(false);
			string? branchBase = review is { PrNumber: > 0 } || defaultRef is null ? review?.MergeBase
				: await git.MergeBaseAsync(worktree, defaultRef, "HEAD", ct).ConfigureAwait(false);
			if (branchBase is null) {
				return ReviewCommitsSnapshot.None;
			}

			var branch = await git.LogRangeAsync(worktree, branchBase, "HEAD", ct).ConfigureAwait(false);
			var reviewed = review is null ? []
				: review.MergeBase == branchBase ? branch.Select(c => c.Sha).ToHashSet(StringComparer.Ordinal)
				: (await git.LogRangeAsync(worktree, review.MergeBase, "HEAD", ct).ConfigureAwait(false)).Select(c => c.Sha).ToHashSet(StringComparer.Ordinal);
			bool wholeBranch = review is not null && (review.PrNumber > 0 || review.Label == $"vs {defaultRef}");
			return new(wholeBranch, [.. branch.Select(c => new BranchCommit(c.Sha, c.Author, c.TimeUnix, c.Summary, reviewed.Contains(c.Sha)))], null);
		} catch (GitException ex) {
			return new(false, [], ex.Message);
		}
	}
}

/// <summary>A branch commit and whether the session's review spans it.</summary>
internal sealed record BranchCommit(string Sha, string Author, long TimeUnix, string Summary, bool InReview);

/// <summary>
/// The branch's commits since it left its base, newest first; <see cref="WholeBranch"/> marks a review comparing the
/// whole branch with its PR's or the repository's default branch.
/// </summary>
internal sealed record ReviewCommitsSnapshot(bool WholeBranch, IReadOnlyList<BranchCommit> Commits, string? Error) {
	public static ReviewCommitsSnapshot None { get; } = new(false, [], null);

	public bool Equals(ReviewCommitsSnapshot? other) =>
		other is not null && WholeBranch == other.WholeBranch && Error == other.Error && Commits.SequenceEqual(other.Commits);

	public override int GetHashCode() => HashCode.Combine(WholeBranch, Error, Commits.Count);
}
