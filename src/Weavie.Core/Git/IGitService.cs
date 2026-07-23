namespace Weavie.Core.Git;

/// <summary>
/// How dirty a worktree is, for escalating a delete confirmation. A worktree with both tracked changes
/// and untracked files reports <see cref="Modified"/>.
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
/// A worktree's change classification plus the tracked and untracked changes a delete would discard — so the
/// confirm can name the first few of them.
/// </summary>
/// <param name="State">How dirty the worktree is.</param>
/// <param name="TrackedFiles">Repo-relative paths with tracked changes, path-ordered.</param>
/// <param name="UntrackedFiles">Repo-relative paths of the untracked files, path-ordered.</param>
public sealed record WorktreeChangeStatus(
	WorktreeChangeState State,
	IReadOnlyList<string> TrackedFiles,
	IReadOnlyList<string> UntrackedFiles);

/// <summary>
/// The git operations Weavie's worktree-per-session feature needs, behind an interface so the worktree
/// manager can be unit-tested against a fake. The real <see cref="GitService"/> shells out to <c>git</c>.
/// All methods throw <see cref="GitException"/> on unexpected failures rather than returning a default.
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
	/// The short names of all local branches, for offering an existing branch to check out as a session.
	/// Remote-tracking branches are out of scope.
	/// </summary>
	Task<IReadOnlyList<string>> ListBranchesAsync(string directory, CancellationToken ct = default);

	/// <summary>
	/// Every ref a diff can name — local branches then remote-tracking branches (e.g. <c>main</c>,
	/// <c>origin/main</c>), minus each remote's symbolic <c>HEAD</c>. Unlike <see cref="ListBranchesAsync"/>
	/// this is a diff target, not a checkout target, so it includes remotes and the checked-out branch.
	/// </summary>
	Task<IReadOnlyList<string>> ListRefsAsync(string directory, CancellationToken ct = default);

	/// <summary>
	/// The repository's default branch — <c>origin/HEAD</c>'s target if set, else <c>main</c> or
	/// <c>master</c> if present, else <c>null</c>. The "branch off main" base.
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
	/// Creates a worktree at <paramref name="worktreePath"/> checked out on the existing branch
	/// <paramref name="branch"/>, so commits flow onto it. Throws if the branch doesn't exist or is
	/// already checked out in another worktree.
	/// </summary>
	Task AttachWorktreeAsync(string repositoryDirectory, string worktreePath, string branch, CancellationToken ct = default);

	/// <summary>
	/// Removes the worktree at <paramref name="worktreePath"/>. When <paramref name="force"/> is false git
	/// refuses a dirty tree; the manager checks first so it can warn the user before forcing.
	/// </summary>
	Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default);

	/// <summary>True when <paramref name="worktreeDirectory"/> has uncommitted changes (tracked or untracked).</summary>
	Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default);

	/// <summary>
	/// Classifies <paramref name="worktreeDirectory"/>'s working tree (clean / untracked-only / modified) and
	/// lists its tracked and untracked changes, so a delete confirm can name what it would discard.
	/// </summary>
	Task<WorktreeChangeStatus> GetChangeStateAsync(string worktreeDirectory, CancellationToken ct = default);

	/// <summary>True when <paramref name="branch"/> is an ancestor of <paramref name="into"/> (fully merged).</summary>
	Task<bool> IsBranchMergedAsync(string repositoryDirectory, string branch, string into, CancellationToken ct = default);

	/// <summary>Deletes the local branch <paramref name="branch"/> (<c>-d</c>, or <c>-D</c> when <paramref name="force"/>).</summary>
	Task DeleteBranchAsync(string repositoryDirectory, string branch, bool force, CancellationToken ct = default);

	/// <summary>
	/// Fetches <paramref name="refName"/> from <paramref name="remote"/> (<c>git fetch &lt;remote&gt; &lt;ref&gt;</c>),
	/// so a PR's head branch exists locally before a worktree checks it out.
	/// </summary>
	Task FetchAsync(string repositoryDirectory, string remote, string refName, CancellationToken ct = default);

	/// <summary>The configured URL of <paramref name="remote"/> (<c>git remote get-url</c>), or <c>null</c> when it has none.</summary>
	Task<string?> GetRemoteUrlAsync(string repositoryDirectory, string remote, CancellationToken ct = default);

	/// <summary>The merge-base (common ancestor) commit of <paramref name="a"/> and <paramref name="b"/>, or <c>null</c> when they share none.</summary>
	Task<string?> MergeBaseAsync(string repositoryDirectory, string a, string b, CancellationToken ct = default);

	/// <summary>
	/// Resolves <paramref name="reference"/> (a branch, tag, commit, or expression like <c>HEAD^</c>) to a full
	/// commit SHA (<c>git rev-parse --verify &lt;ref&gt;^{commit}</c>), or <c>null</c> when it names no commit here.
	/// Safe for web-supplied input: a name that could read as an option resolves to <c>null</c>, never reaches git.
	/// </summary>
	Task<string?> ResolveCommitAsync(string repositoryDirectory, string reference, CancellationToken ct = default);

	/// <summary>
	/// The files that differ between <paramref name="baseRef"/> and the working tree — <c>git diff &lt;base&gt;</c>
	/// plus untracked-but-not-ignored files (all-added) — with added/removed line counts, path-ordered. The
	/// changed-file list for a "diff against" review.
	/// </summary>
	Task<IReadOnlyList<DiffFileChange>> DiffWorktreeAsync(string repositoryDirectory, string baseRef, CancellationToken ct = default);

	/// <summary>
	/// The files changed between <paramref name="fromRef"/> and <paramref name="toRef"/> (<c>git diff --numstat</c>),
	/// each with its added/removed line counts — the changed-file list for a PR's diff walk.
	/// </summary>
	Task<IReadOnlyList<DiffFileChange>> DiffRefsAsync(string repositoryDirectory, string fromRef, string toRef, CancellationToken ct = default);

	/// <summary>
	/// The content, existence, and text classification of <paramref name="path"/> at <paramref name="reference"/>
	/// (<c>git show ref:path</c>) — the exact baseline for a PR/ref review file.
	/// </summary>
	Task<GitFileContent> ReadFileAtRefAsync(string repositoryDirectory, string reference, string path, CancellationToken ct = default);
}
