namespace Weavie.Core.Worktrees;

/// <summary>
/// Persisted metadata for one Weavie-owned worktree. The managed directory remains authoritative for ownership
/// if this recoverable record is lost.
/// </summary>
public sealed record WorktreeRecord {
	/// <summary>The branch git last reported checked out here; refreshed whenever Weavie lists worktrees.</summary>
	public required string Branch { get; init; }

	/// <summary>Absolute path to the worktree's working directory (normalized).</summary>
	public required string Path { get; init; }

	/// <summary>When Weavie created the worktree (UTC).</summary>
	public required DateTimeOffset CreatedAtUtc { get; init; }

	/// <summary>The agent provider this worktree-backed session was created with.</summary>
	public required string AgentProviderId { get; init; }
}
