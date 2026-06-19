namespace Weavie.Core.Worktrees;

/// <summary>
/// The reconciled state of one worktree: what git reports cross-checked against the
/// <see cref="WorktreeRegistry"/>. Surfacing every worktree — managed, primary, orphaned, or
/// externally-created — is what keeps worktrees from leaking unnoticed.
/// </summary>
public sealed record WorktreeStatus {
	/// <summary>Absolute path to the worktree's working directory.</summary>
	public required string Path { get; init; }

	/// <summary>The short branch name checked out here, or <c>null</c> when detached.</summary>
	public string? Branch { get; init; }

	/// <summary>The ref this worktree's branch was started from, when Weavie created it.</summary>
	public string? BaseRef { get; init; }

	/// <summary>True when this worktree is in Weavie's registry (Weavie created/tracks it).</summary>
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

	/// <summary>A Weavie-created worktree git no longer knows about — a stale registry row to prune.</summary>
	public bool IsOrphan => IsManaged && !Exists;

	/// <summary>A worktree git reports that Weavie did not create (external, or its registry row was lost) — surfaced, not hidden.</summary>
	public bool IsUntracked => !IsManaged && !IsPrimary && Exists;

	/// <summary>Removable without losing work: present, not the primary checkout, clean, and fully merged.</summary>
	public bool IsSafeToRemove => Exists && !IsPrimary && !IsDirty && IsMerged;
}
