namespace Weavie.Core.Worktrees;

/// <summary>
/// The outcome of a <see cref="WorktreeManager.ReconcileAsync"/> pass: orphaned rows pruned, untracked
/// worktrees git reports, and the resulting statuses. Reconcile fixes bookkeeping only, never removing a live worktree.
/// </summary>
public sealed record WorktreeReconcileReport {
	/// <summary>Registry rows dropped because their worktree no longer exists in git.</summary>
	public required int OrphansPruned { get; init; }

	/// <summary>Worktrees git reports that Weavie did not create (surfaced so nothing is hidden).</summary>
	public required int Untracked { get; init; }

	/// <summary>The worktree statuses after reconciling.</summary>
	public required IReadOnlyList<WorktreeStatus> Statuses { get; init; }
}
