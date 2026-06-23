namespace Weavie.Core.Worktrees;

/// <summary>
/// One worktree Weavie created, as recorded in the per-workspace <see cref="WorktreeRegistry"/> — the source
/// of truth for "Weavie made this," reconciled against git.
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
}
