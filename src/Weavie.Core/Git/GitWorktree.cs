namespace Weavie.Core.Git;

/// <summary>
/// One entry from <c>git worktree list --porcelain</c>: a working tree linked to the repository. The
/// repository's primary checkout is the first entry; additional ones are Weavie's per-session trees.
/// </summary>
public sealed record GitWorktree {
	/// <summary>Absolute path to the worktree's working directory.</summary>
	public required string Path { get; init; }

	/// <summary>The commit the worktree's HEAD points at, or <c>null</c> for a bare entry.</summary>
	public string? Head { get; init; }

	/// <summary>The short branch name checked out here, or <c>null</c> when detached or bare.</summary>
	public string? Branch { get; init; }

	/// <summary>True for the repository's bare entry (no working tree).</summary>
	public bool IsBare { get; init; }

	/// <summary>True when the worktree is on a detached HEAD rather than a branch.</summary>
	public bool IsDetached { get; init; }

	/// <summary>True when the worktree is locked against pruning.</summary>
	public bool IsLocked { get; init; }

	/// <summary>True when the worktree's working directory is missing or stale.</summary>
	public bool IsPrunable { get; init; }
}
