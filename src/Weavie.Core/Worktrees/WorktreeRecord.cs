namespace Weavie.Core.Worktrees;

/// <summary>
/// Persisted metadata for one Weavie-owned worktree. The managed directory remains authoritative for ownership
/// if this recoverable record is lost.
/// </summary>
public sealed record WorktreeRecord {
	/// <summary>The branch checked out in this worktree (created together with it).</summary>
	public required string Branch { get; init; }

	/// <summary>Absolute path to the worktree's working directory (normalized).</summary>
	public required string Path { get; init; }

	/// <summary>The ref the worktree's branch was started from (the source session's HEAD or <c>main</c>).</summary>
	public required string BaseRef { get; init; }

	/// <summary>When Weavie created the worktree (UTC).</summary>
	public required DateTimeOffset CreatedAtUtc { get; init; }

	/// <summary>The agent provider this worktree-backed session was created with.</summary>
	public required string AgentProviderId { get; init; }
}
