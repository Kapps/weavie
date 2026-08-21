namespace Weavie.Core.Worktrees;

/// <summary>
/// The reconciled state of one worktree: what git reports cross-checked against the
/// <see cref="WorktreeRegistry"/>, so no worktree leaks unnoticed.
/// </summary>
public sealed record WorktreeStatus {
	/// <summary>Absolute path to the worktree's working directory.</summary>
	public required string Path { get; init; }

	/// <summary>The short branch name checked out here, or <c>null</c> when detached.</summary>
	public string? Branch { get; init; }

	/// <summary>The ref this worktree's branch was started from, when Weavie created it.</summary>
	public string? BaseRef { get; init; }

	/// <summary>The agent provider Weavie recorded for this worktree-backed session, when known.</summary>
	public string? AgentProviderId { get; init; }

	/// <summary>True when Weavie owns the checkout by its managed location or registry record.</summary>
	public required bool IsManaged { get; init; }

	/// <summary>True for the workspace's primary checkout (the folder the user opened) — never auto-removed.</summary>
	public required bool IsPrimary { get; init; }

	/// <summary>True when the worktree's working directory is present and known to git.</summary>
	public required bool Exists { get; init; }

	/// <summary>True when the worktree has uncommitted changes (tracked or untracked).</summary>
	public required bool IsDirty { get; init; }

	/// <summary>True when the worktree's branch is fully merged into the repository's default branch.</summary>
	public required bool IsMerged { get; init; }

	/// <summary>When Weavie created the worktree (UTC), when known.</summary>
	public DateTimeOffset? CreatedAtUtc { get; init; }

	/// <summary>An owned worktree git does not presently report as live.</summary>
	public bool IsOrphan => IsManaged && !Exists;

	/// <summary>A worktree git reports outside Weavie's managed directory with no ownership record.</summary>
	public bool IsUntracked => !IsManaged && !IsPrimary && Exists;

	/// <summary>Removable without losing work: present, not the primary checkout, clean, and fully merged.</summary>
	public bool IsSafeToRemove => Exists && !IsPrimary && !IsDirty && IsMerged;
}
