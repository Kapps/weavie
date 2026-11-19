namespace Weavie.Core.Git;

/// <summary>The git operation under way in a worktree (see <see cref="GitService.ReadOperationAsync"/>).</summary>
public enum GitOperation {
	/// <summary>Nothing is rewriting the worktree.</summary>
	None,

	/// <summary>A command holds the index lock; it ends without a later HEAD or index change when it only read.</summary>
	Locked,

	/// <summary>A rebase, <c>git am</c>, bisect, or conflicted merge is stopped mid-way until the user finishes it.</summary>
	Stopped,
}
