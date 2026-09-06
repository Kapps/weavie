using System.Text.Json.Serialization;

namespace Weavie.Core.Review;

/// <summary>The source of a worktree's durable review; head updates do not create a new review.</summary>
public sealed record ReviewContext(int PrNumber, string Label, string HeadRef, string MergeBase, string HeadSha, RepoRef? Repo, string Worktree) {
	private volatile IReadOnlyList<ReviewComment> _comments = [];
	/// <summary>Live forge comments, refreshed independently of local review decisions.</summary>
	[JsonIgnore]
	public IReadOnlyList<ReviewComment> Comments { get => _comments; set => _comments = value; }
	/// <summary>Whether two openings refer to the same logical review.</summary>
	public bool SameSource(ReviewContext other) => PrNumber == other.PrNumber && Repo == other.Repo
		&& (PrNumber > 0 || Label == other.Label);
}

/// <summary>A path's ref and working-tree snapshots, read before arming a review.</summary>
public sealed record ReviewSeed(string Path, string Baseline, string Current, bool BaselineExists, bool CurrentExists);
