namespace Weavie.Core.Review;

/// <summary>
/// The source of a worktree's git review: its working tree compared with <c>Target</c> from their merge-base. A moved
/// merge-base re-lists the review; another branch checked out closes it. See docs/specs/review-state.md.
/// </summary>
public sealed record ReviewContext(
	int PrNumber, string Label, string MergeBase, RepoRef? Repo, string Worktree, string Target, string Branch) {
	/// <summary>Whether two openings refer to the same logical review.</summary>
	public bool SameSource(ReviewContext other) => PrNumber == other.PrNumber && Repo == other.Repo
		&& Branch == other.Branch && (PrNumber > 0 || Label == other.Label);
}

/// <summary>A path's before at the review's base and its working-tree snapshot, read for a review listing.</summary>
public sealed record ReviewSeed(string Path, string Baseline, string Current, bool BaselineExists, bool CurrentExists);
