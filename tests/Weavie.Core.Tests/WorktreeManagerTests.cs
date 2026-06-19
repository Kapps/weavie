using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="WorktreeManager"/>'s orchestration and — the heart of the no-leak guarantee —
/// its classification of every worktree against the registry: managed/primary/orphan/untracked,
/// dirty/merged, the dirty-removal guard, and reconcile pruning. Uses a <see cref="FakeGitService"/> so
/// the logic is exercised deterministically without a real repository.
/// </summary>
public sealed class WorktreeManagerTests {
	private const string RegistryPath = "/weavie-wt-mgr-tests/worktrees.json";
	private static readonly string RepoRoot = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "repo");
	private static readonly string WorktreesDir = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "worktrees");

	private static (WorktreeManager Manager, WorktreeRegistry Registry, FakeGitService Git) NewManager() {
		var registry = new WorktreeRegistry(new InMemoryFileSystem(), RegistryPath);
		var git = new FakeGitService { DefaultBranch = "main" };
		git.Worktrees.Add(new GitWorktree { Path = RepoRoot, Branch = "main", Head = "primary" });
		var manager = new WorktreeManager(git, registry, RepoRoot, WorktreesDir);
		return (manager, registry, git);
	}

	[Fact]
	public async Task Create_AddsWorktreeAndRecord() {
		var (manager, registry, git) = NewManager();

		var record = await manager.CreateAsync("feature", "main");

		Assert.Equal("feature", record.Branch);
		Assert.StartsWith(WorktreesDir, record.Path);
		Assert.Single(registry.Items);
		Assert.Contains(git.Worktrees, w => w.Branch == "feature");
		Assert.Contains("feature", git.Branches);
	}

	[Fact]
	public async Task Create_ExistingBranch_Throws() {
		var (manager, _, git) = NewManager();
		git.Branches.Add("feature");

		await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync("feature", "main"));
	}

	[Fact]
	public async Task List_ClassifiesManagedPrimaryDirtyMergedOrphanUntracked() {
		var (manager, registry, git) = NewManager();

		// Managed, clean, merged -> safe to remove.
		string donePath = Path.Combine(WorktreesDir, "done");
		git.Worktrees.Add(new GitWorktree { Path = donePath, Branch = "done", Head = "d1" });
		registry.Add(new WorktreeRecord { Branch = "done", Path = donePath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.MergedBranches.Add("done");

		// Managed, dirty, unmerged -> not safe.
		string wipPath = Path.Combine(WorktreesDir, "wip");
		git.Worktrees.Add(new GitWorktree { Path = wipPath, Branch = "wip", Head = "w1" });
		registry.Add(new WorktreeRecord { Branch = "wip", Path = wipPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.DirtyPaths.Add(wipPath);

		// Untracked: git knows it, Weavie's registry does not.
		string extPath = Path.Combine(WorktreesDir, "external");
		git.Worktrees.Add(new GitWorktree { Path = extPath, Branch = "external", Head = "e1" });

		// Orphan: registry has it, git no longer does.
		string gonePath = Path.Combine(WorktreesDir, "gone");
		registry.Add(new WorktreeRecord { Branch = "gone", Path = gonePath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });

		var list = await manager.ListAsync();

		var primary = list.Single(s => s.IsPrimary);
		Assert.False(primary.IsSafeToRemove);

		var done = list.Single(s => s.Branch == "done");
		Assert.True(done.IsManaged);
		Assert.True(done.IsMerged);
		Assert.False(done.IsDirty);
		Assert.True(done.IsSafeToRemove);

		var wip = list.Single(s => s.Branch == "wip");
		Assert.True(wip.IsDirty);
		Assert.False(wip.IsMerged);
		Assert.False(wip.IsSafeToRemove);

		var external = list.Single(s => s.Branch == "external");
		Assert.True(external.IsUntracked);
		Assert.False(external.IsManaged);

		var gone = list.Single(s => s.Branch == "gone");
		Assert.True(gone.IsOrphan);
		Assert.False(gone.Exists);
	}

	[Fact]
	public async Task Remove_Dirty_WithoutForce_Throws_AndKeepsWorktree() {
		var (manager, registry, git) = NewManager();
		string wipPath = Path.Combine(WorktreesDir, "wip");
		git.Worktrees.Add(new GitWorktree { Path = wipPath, Branch = "wip", Head = "w1" });
		registry.Add(new WorktreeRecord { Branch = "wip", Path = wipPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.DirtyPaths.Add(wipPath);

		await Assert.ThrowsAsync<WorktreeDirtyException>(() => manager.RemoveAsync(wipPath, deleteBranch: false, force: false));

		Assert.Contains(git.Worktrees, w => w.Branch == "wip");
		Assert.NotNull(registry.FindByBranch("wip"));
	}

	[Fact]
	public async Task Remove_Dirty_WithForce_RemovesAndDeletesBranch() {
		var (manager, registry, git) = NewManager();
		string wipPath = Path.Combine(WorktreesDir, "wip");
		git.Worktrees.Add(new GitWorktree { Path = wipPath, Branch = "wip", Head = "w1" });
		git.Branches.Add("wip");
		registry.Add(new WorktreeRecord { Branch = "wip", Path = wipPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.DirtyPaths.Add(wipPath);

		await manager.RemoveAsync(wipPath, deleteBranch: true, force: true);

		Assert.DoesNotContain(git.Worktrees, w => w.Branch == "wip");
		Assert.DoesNotContain("wip", git.Branches);
		Assert.Null(registry.FindByBranch("wip"));
	}

	[Fact]
	public async Task Reconcile_PrunesOrphans_AndCountsUntracked() {
		var (manager, registry, git) = NewManager();

		string extPath = Path.Combine(WorktreesDir, "external");
		git.Worktrees.Add(new GitWorktree { Path = extPath, Branch = "external", Head = "e1" });

		string gonePath = Path.Combine(WorktreesDir, "gone");
		registry.Add(new WorktreeRecord { Branch = "gone", Path = gonePath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });

		var report = await manager.ReconcileAsync();

		Assert.Equal(1, report.OrphansPruned);
		Assert.Equal(1, report.Untracked);
		Assert.Null(registry.FindByBranch("gone"));
		Assert.DoesNotContain(report.Statuses, s => s.Branch == "gone");
	}

	/// <summary>An in-memory <see cref="IGitService"/> with controllable branches, worktrees, dirty paths, and merge state.</summary>
	private sealed class FakeGitService : IGitService {
		public HashSet<string> Branches { get; } = new(StringComparer.Ordinal);

		public List<GitWorktree> Worktrees { get; } = [];

		public HashSet<string> DirtyPaths { get; } = new(StringComparer.Ordinal);

		public HashSet<string> MergedBranches { get; } = new(StringComparer.Ordinal);

		public string? DefaultBranch { get; set; }

		public Task<bool> IsRepositoryAsync(string directory, CancellationToken ct = default) => Task.FromResult(true);

		public Task<string> GetHeadCommitAsync(string directory, CancellationToken ct = default) => Task.FromResult("0000000");

		public Task<string?> GetCurrentBranchAsync(string directory, CancellationToken ct = default) => Task.FromResult(DefaultBranch);

		public Task<bool> BranchExistsAsync(string directory, string branch, CancellationToken ct = default) => Task.FromResult(Branches.Contains(branch));

		public Task<string?> ResolveDefaultBranchAsync(string directory, CancellationToken ct = default) => Task.FromResult(DefaultBranch);

		public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<GitWorktree>>([.. Worktrees]);

		public Task AddWorktreeAsync(string repositoryDirectory, string worktreePath, string newBranch, string baseRef, CancellationToken ct = default) {
			Branches.Add(newBranch);
			Worktrees.Add(new GitWorktree { Path = worktreePath, Branch = newBranch, Head = "1111111" });
			return Task.CompletedTask;
		}

		public Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default) {
			Worktrees.RemoveAll(w => PathEquals(w.Path, worktreePath));
			return Task.CompletedTask;
		}

		public Task PruneWorktreesAsync(string repositoryDirectory, CancellationToken ct = default) => Task.CompletedTask;

		public Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default) =>
			Task.FromResult(DirtyPaths.Any(p => PathEquals(p, worktreeDirectory)));

		public Task<bool> IsBranchMergedAsync(string repositoryDirectory, string branch, string into, CancellationToken ct = default) =>
			Task.FromResult(MergedBranches.Contains(branch));

		public Task DeleteBranchAsync(string repositoryDirectory, string branch, bool force, CancellationToken ct = default) {
			Branches.Remove(branch);
			return Task.CompletedTask;
		}

		private static bool PathEquals(string a, string b) =>
			string.Equals(
				Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
	}
}
