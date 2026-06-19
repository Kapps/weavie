namespace Weavie.Core.Git;

/// <summary>
/// One entry from <c>git worktree list --porcelain</c>: a working tree linked to the repository. The
/// repository's primary checkout is itself a worktree (the first entry); additional linked worktrees are
/// the per-session trees Weavie manages.
/// </summary>
public sealed record GitWorktree {
	/// <summary>Absolute path to the worktree's working directory (as git reports it).</summary>
	public required string Path { get; init; }

	/// <summary>The commit the worktree's HEAD points at, or <c>null</c> for a bare entry.</summary>
	public string? Head { get; init; }

	/// <summary>The short branch name checked out here, or <c>null</c> when detached or bare.</summary>
	public string? Branch { get; init; }

	/// <summary>True for the repository's bare entry (no working tree).</summary>
	public bool IsBare { get; init; }

	/// <summary>True when the worktree is on a detached HEAD rather than a branch.</summary>
	public bool IsDetached { get; init; }

	/// <summary>True when git reports the worktree as locked.</summary>
	public bool IsLocked { get; init; }

	/// <summary>True when git reports the worktree as prunable (its working directory is missing/stale).</summary>
	public bool IsPrunable { get; init; }
}
