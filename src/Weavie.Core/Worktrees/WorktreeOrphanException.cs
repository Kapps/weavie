namespace Weavie.Core.Worktrees;

/// <summary>
/// Thrown by <see cref="WorktreeManager.RemoveAsync"/> when git no longer tracks a worktree path, its
/// directory is still on disk, AND that directory sits OUTSIDE the managed worktrees dir — so Weavie can't
/// safely delete it directly (it would be deleting an arbitrary path it doesn't own). A half-removed worktree
/// INSIDE the managed dir is deleted automatically instead; this fires only for the un-ownable case, where the
/// directory must be removed by hand.
/// </summary>
public sealed class WorktreeOrphanException : Exception {
	/// <summary>Creates the exception for the orphaned worktree directory at <paramref name="path"/>.</summary>
	public WorktreeOrphanException(string path)
		: base($"Worktree '{path}' is half-removed: git no longer tracks it, but the directory still exists on disk. "
			+ "It is outside Weavie's managed worktrees directory, so Weavie won't delete it automatically — "
			+ "remove the directory manually to finish.") {
		Path = path;
	}

	/// <summary>The orphaned worktree's directory path.</summary>
	public string Path { get; }
}
