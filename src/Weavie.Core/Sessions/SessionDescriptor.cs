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
}
