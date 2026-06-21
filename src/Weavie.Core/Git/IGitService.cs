namespace Weavie.Core.Git;

/// <summary>
/// How dirty a worktree's working tree is, for escalating a delete confirmation: nothing to lose, only
/// untracked (uncommitted, unindexed) files would be deleted, or tracked changes (modified/staged/deleted)
/// would be lost. A worktree with both tracked changes and untracked files reports <see cref="Modified"/>.
/// </summary>
public enum WorktreeChangeState {
	/// <summary>Clean working tree — deleting the worktree loses nothing uncommitted.</summary>
	Clean,

	/// <summary>Only untracked files would be deleted with the worktree.</summary>
	UntrackedOnly,

	/// <summary>Tracked changes (modified/staged/deleted) would be lost; possibly untracked files too.</summary>
	Modified,
}

/// <summary>
/// The git operations Weavie's worktree-per-session feature needs, behind an interface so the worktree
/// manager's orchestration can be unit-tested against a fake while the real <see cref="GitService"/>
/// shells out to the <c>git</c> executable. All methods throw <see cref="GitException"/> on unexpected
/// failures rather than returning a silent default.
/// </summary>
public interface IGitService {
	/// <summary>True when <paramref name="directory"/> is inside a git work tree.</summary>
	Task<bool> IsRepositoryAsync(string directory, CancellationToken ct = default);

	/// <summary>The full commit SHA that <c>HEAD</c> resolves to in <paramref name="directory"/>.</summary>
	Task<string> GetHeadCommitAsync(string directory, CancellationToken ct = default);

	/// <summary>The short branch name checked out in <paramref name="directory"/>, or <c>null</c> when detached.</summary>
	Task<string?> GetCurrentBranchAsync(string directory, CancellationToken ct = default);

	/// <summary>True when a local branch named <paramref name="branch"/> exists.</summary>
	Task<bool> BranchExistsAsync(string directory, string branch, CancellationToken ct = default);

	/// <summary>
	/// The short names of all local branches (<c>git for-each-ref refs/heads</c>), for offering an existing
	/// branch to check out as a session. Remote-tracking branches are out of scope for now.
	/// </summary>
	Task<IReadOnlyList<string>> ListBranchesAsync(string directory, CancellationToken ct = default);

	/// <summary>
	/// The repository's default branch — <c>origin/HEAD</c>'s target if set, else <c>main</c> or
	/// <c>master</c> if present, else <c>null</c>. Used as the "branch off main" base.
	/// </summary>
	Task<string?> ResolveDefaultBranchAsync(string directory, CancellationToken ct = default);

	/// <summary>All worktrees linked to the repository containing <paramref name="directory"/>.</summary>
	Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken ct = default);

	/// <summary>
	/// Creates a new worktree at <paramref name="worktreePath"/> on a new branch <paramref name="newBranch"/>
	/// started from <paramref name="baseRef"/> (<c>git worktree add -b</c>). Throws if the branch already
	/// exists or is checked out elsewhere.
	/// </summary>
	Task AddWorktreeAsync(string repositoryDirectory, string worktreePath, string newBranch, string baseRef, CancellationToken ct = default);

	/// <summary>
	/// Creates a worktree at <paramref name="worktreePath"/> checked out on the <em>existing</em> branch
	/// <paramref name="branch"/> (<c>git worktree add</c> with no <c>-b</c>), so commits flow onto that branch.
	/// Throws if the branch doesn't exist or is already checked out in another worktree.
	/// </summary>
	Task AttachWorktreeAsync(string repositoryDirectory, string worktreePath, string branch, CancellationToken ct = default);

	/// <summary>
	/// Removes the worktree at <paramref name="worktreePath"/> (<c>git worktree remove</c>). When
	/// <paramref name="force"/> is false git refuses if the tree is dirty; the manager checks first so it
	/// can warn the user before forcing.
	/// </summary>
	Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default);

	/// <summary>Prunes administrative entries for worktrees whose directories were removed out-of-band.</summary>
	Task PruneWorktreesAsync(string repositoryDirectory, CancellationToken ct = default);

	/// <summary>True when <paramref name="worktreeDirectory"/> has uncommitted changes (tracked or untracked).</summary>
	Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default);

	/// <summary>Classifies <paramref name="worktreeDirectory"/>'s working tree (clean / untracked-only / modified).</summary>
	Task<WorktreeChangeState> GetChangeStateAsync(string worktreeDirectory, CancellationToken ct = default);

	/// <summary>True when <paramref name="branch"/> is an ancestor of <paramref name="into"/> (fully merged).</summary>
	Task<bool> IsBranchMergedAsync(string repositoryDirectory, string branch, string into, CancellationToken ct = default);

	/// <summary>Deletes the local branch <paramref name="branch"/> (<c>-d</c>, or <c>-D</c> when <paramref name="force"/>).</summary>
	Task DeleteBranchAsync(string repositoryDirectory, string branch, bool force, CancellationToken ct = default);
}
