namespace Weavie.Core.Sessions;

/// <summary>
/// The persisted description of one session in a workspace. Runtime status is not persisted — it is derived
/// live by <see cref="SessionStatusMachine"/>.
/// </summary>
public sealed record SessionDescriptor {
	/// <summary>This session's stable identity.</summary>
	public required SessionId Id { get; init; }

	/// <summary>The rail label — typically the session's branch name.</summary>
	public required string Label { get; init; }

	/// <summary>The session's working directory (its git worktree, or the workspace root for the primary).</summary>
	public required string WorktreePath { get; init; }

	/// <summary>True for the workspace's primary session (the folder the user opened; not a Weavie-created worktree).</summary>
	public required bool IsPrimary { get; init; }

	/// <summary>Whether this session had a live backend at last persist — the flag a reopen restores (loading + <c>--resume</c>ing it).</summary>
	public required bool Loaded { get; init; }

	/// <summary>The agent provider persisted with this session. Existing documents without a value are Claude.</summary>
	public required string AgentProviderId { get; init; }
}
