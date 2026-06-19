namespace Weavie.Core.Worktrees;

/// <summary>
/// Thrown by <see cref="WorktreeManager.RemoveAsync"/> when a worktree has uncommitted changes and
/// removal was not forced — so the host can warn the user before discarding work rather than silently
/// destroying it.
/// </summary>
public sealed class WorktreeDirtyException : Exception {
	/// <summary>Creates the exception for the dirty worktree at <paramref name="path"/>.</summary>
	public WorktreeDirtyException(string path)
		: base($"Worktree '{path}' has uncommitted changes; refusing to remove without force.") {
		Path = path;
	}

	/// <summary>The dirty worktree's path.</summary>
	public string Path { get; }
}
