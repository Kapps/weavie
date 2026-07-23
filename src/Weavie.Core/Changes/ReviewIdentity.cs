using Weavie.Core.Review;

namespace Weavie.Core.Changes;

/// <summary>The durable identity of a PR or ref review armed over a worktree.</summary>
public sealed record ReviewIdentity(
	int PrNumber,
	string Label,
	string HeadRef,
	string MergeBase,
	string HeadSha,
	RepoRef? Repo,
	string Worktree);

/// <summary>One file used to atomically seed a ref review.</summary>
public sealed record ReviewSeed(
	string Path,
	string RefContent,
	string DiskContent,
	bool ExistedAtRef,
	bool ExistsOnDisk);

/// <summary>A durable-review restore or persistence problem that must be shown to the user.</summary>
public sealed record ReviewProblem(string Path, string Message);
