namespace Weavie.Core.Worktrees;

/// <summary>
/// Thrown by <see cref="WorktreeManager.RemoveAsync"/> when git no longer tracks a worktree path but its
/// directory is still on disk — a half-removed worktree left behind when an earlier removal stripped git's
/// own record but couldn't unlink the files (a lingering file lock). There is no git operation that can
/// finish the removal (<c>git worktree remove</c> only answers "is not a working tree"), so this surfaces
/// loudly instead of silently dropping the registry row and leaking the directory. Recovering it requires
/// deleting the directory directly, which Weavie does not (yet) do.
/// </summary>
public sealed class WorktreeOrphanException : Exception {
	/// <summary>Creates the exception for the orphaned worktree directory at <paramref name="path"/>.</summary>
	public WorktreeOrphanException(string path)
		: base($"Worktree '{path}' is half-removed: git no longer tracks it, but the directory still exists on disk "
			+ "(an earlier delete couldn't unlink it). Remove the directory manually to finish.") {
		Path = path;
	}

	/// <summary>The orphaned worktree's directory path.</summary>
	public string Path { get; }
}
