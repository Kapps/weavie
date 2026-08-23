namespace Weavie.Core.Worktrees;

/// <summary>
/// Git's constraints on removing one checkout: whether git will remove it at all, and whether anything keeps the
/// commits made there once it is gone.
/// </summary>
public sealed record WorktreeRemoval {
	/// <summary>True when git reports a live worktree at the path.</summary>
	public required bool Exists { get; init; }

	/// <summary>The repository's main working tree, which git refuses to remove.</summary>
	public required bool IsMainCheckout { get; init; }

	/// <summary>Locked against removal; git refuses until it is unlocked.</summary>
	public required bool IsLocked { get; init; }

	/// <summary>On a detached HEAD, so no branch keeps the commits made here after removal.</summary>
	public required bool IsDetached { get; init; }
}
