using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorktreeManager"/> orchestration and its classification of every worktree against the
/// registry: managed/primary/orphan/untracked, dirty/merged, the dirty-removal guard, and reconcile
/// pruning. Uses a <see cref="FakeGitService"/> for deterministic logic without a real repository.
/// </summary>
public sealed class WorktreeManagerTests {
	private const string RegistryPath = "/weavie-wt-mgr-tests/worktrees.json";
	private static readonly string RepoRoot = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "repo");
	private static readonly string WorktreesDir = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "worktrees");

	private static (WorktreeManager Manager, WorktreeRegistry Registry, FakeGitService Git) NewManager() {
		var registry = new WorktreeRegistry(new InMemoryFileSystem(), RegistryPath);
		var git = new FakeGitService { DefaultBranch = "main" };
		git.Worktrees.Add(new GitWorktree { Path = RepoRoot, Branch = "main", Head = "primary" });
		var manager = new WorktreeManager(git, registry, RepoRoot, WorktreesDir, NullWorktreeProvisioner.Instance);
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
	public async Task Create_WithProvider_RecordsProvider() {
		var (manager, registry, _) = NewManager();

		var record = await manager.CreateAsync("feature", "main", "codex");

		Assert.Equal("codex", record.AgentProviderId);
		Assert.Equal("codex", Assert.Single(registry.Items).AgentProviderId);
	}

	[Fact]
	public async Task Create_SlugCollision_AllocatesSuffixedPath() {
		var (manager, _, _) = NewManager();

		// Two distinct branches that slugify to the same leaf must land in distinct directories: the second
		// gets a "-2" suffix rather than colliding on the first's path.
		var first = await manager.CreateAsync("feature/a", "main");
		var second = await manager.CreateAsync("feature-a", "main");

		Assert.Equal(Path.Combine(WorktreesDir, "feature-a"), first.Path);
		Assert.Equal(Path.Combine(WorktreesDir, "feature-a-2"), second.Path);
	}

	[Fact]
	public async Task Create_BranchSlugifyingToEmpty_FallsBackToSessionSlug() {
		var (manager, _, _) = NewManager();

		// A branch whose every character is stripped by slugification leaves no leaf; the path falls back to
		// the "session" slug rather than landing directly on the worktrees dir.
		var record = await manager.CreateAsync("///", "main");

		Assert.Equal(Path.Combine(WorktreesDir, "session"), record.Path);
	}

	[Fact]
	public async Task Create_ExistingBranch_Throws() {
		var (manager, _, git) = NewManager();
		git.Branches.Add("feature");

		await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CreateAsync("feature", "main"));
	}

	[Fact]
	public async Task Attach_ExistingBranch_AddsWorktreeAndRecord() {
		var (manager, registry, git) = NewManager();
		git.Branches.Add("feature");

		var record = await manager.AttachAsync("feature");

		Assert.Equal("feature", record.Branch);
		Assert.StartsWith(WorktreesDir, record.Path);
		Assert.Single(registry.Items);
		Assert.Contains(git.Worktrees, w => w.Branch == "feature" && w.Path == record.Path);
	}

	[Fact]
	public async Task Attach_WithProvider_RecordsProvider() {
		var (manager, registry, git) = NewManager();
		git.Branches.Add("feature");

		var record = await manager.AttachAsync("feature", "codex");

		Assert.Equal("codex", record.AgentProviderId);
		Assert.Equal("codex", Assert.Single(registry.Items).AgentProviderId);
	}

	[Fact]
	public async Task Attach_MissingBranch_Throws() {
		var (manager, _, _) = NewManager();

		// Attach requires the branch to already exist (inverse of Create's guard).
		await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AttachAsync("nope"));
	}

	[Fact]
	public async Task Attach_AlreadyTracked_ReturnsExistingRecord_WithoutDuplicating() {
		var (manager, registry, git) = NewManager();
		git.Branches.Add("feature");
		var first = await manager.AttachAsync("feature");
		int worktreeCountAfterFirst = git.Worktrees.Count;

		var second = await manager.AttachAsync("feature");

		Assert.Equal(first.Path, second.Path);
		Assert.Single(registry.Items);
		Assert.Equal(worktreeCountAfterFirst, git.Worktrees.Count); // no duplicate git worktree add
	}

	[Fact]
	public async Task Attach_AlreadyTracked_PreservesProvider() {
		var (manager, _, git) = NewManager();
		git.Branches.Add("feature");
		var first = await manager.AttachAsync("feature", "codex");

		var second = await manager.AttachAsync("feature", "claude");

		Assert.Equal(first.Path, second.Path);
		Assert.Equal("codex", second.AgentProviderId);
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

		// Untracked: git knows it, the registry does not.
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
	public async Task List_PrunableGitWorktree_DoesNotProbeMissingDirectory() {
		var (manager, _, git) = NewManager();
		string stalePath = Path.Combine(WorktreesDir, "stale");
		git.Worktrees.Add(new GitWorktree { Path = stalePath, Branch = "stale", Head = "s1", IsPrunable = true });
		git.DirtyProbeFailures.Add(stalePath);

		var list = await manager.ListAsync();

		var stale = list.Single(s => s.Branch == "stale");
		Assert.False(stale.Exists);
		Assert.False(stale.IsDirty);
		Assert.False(stale.IsMerged);
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
	public async Task Remove_TransientFileLock_RetriesThenSucceeds() {
		var (manager, registry, git) = NewManager();
		string wtPath = Path.Combine(WorktreesDir, "locked");
		git.Worktrees.Add(new GitWorktree { Path = wtPath, Branch = "locked", Head = "l1" });
		registry.Add(new WorktreeRecord { Branch = "locked", Path = wtPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		// The first attempt loses to a transient file lock; the bounded retry succeeds.
		git.RemoveWorktreeFailures.Enqueue(new GitException(
			"git worktree remove failed (exit 255): error: failed to delete '...': Directory not empty"));
		var logs = new List<string>();
		manager.Log += logs.Add;

		await manager.RemoveAsync(wtPath, deleteBranch: false, force: false);

		Assert.DoesNotContain(git.Worktrees, w => w.Branch == "locked");
		Assert.Null(registry.FindByBranch("locked"));
		Assert.Contains(logs, l => l.Contains("retrying", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Remove_OwnedWorktree_ClearsContentsKeepingGit_ThenLetsGitFinalize() {
		var (manager, registry, git) = NewManager();
		string wtPath = Path.Combine(WorktreesDir, "owned-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(wtPath);
		File.WriteAllText(Path.Combine(wtPath, ".git"), "gitdir: ../repo/.git/worktrees/owned\n");
		File.WriteAllText(Path.Combine(wtPath, "leftover.txt"), "x");
		Directory.CreateDirectory(Path.Combine(wtPath, "sub"));
		File.WriteAllText(Path.Combine(wtPath, "sub", "nested.txt"), "y");
		var clearedAtRemove = new List<string>();
		try {
			git.Worktrees.Add(new GitWorktree { Path = wtPath, Branch = "owned", Head = "o1" });
			registry.Add(new WorktreeRecord { Branch = "owned", Path = wtPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
			// Capture the on-disk state at the moment git is asked to finalize: the working tree must be cleared
			// down to just the .git link so git's removal runs against an empty tree (no lock race).
			git.OnRemoveWorktree = p => clearedAtRemove.AddRange(Directory.EnumerateFileSystemEntries(p).Select(Path.GetFileName)!);

			await manager.RemoveAsync(wtPath, deleteBranch: false, force: false);

			Assert.Equal([".git"], clearedAtRemove); // only the git link survived our clear; git then removed it
			Assert.False(Directory.Exists(wtPath)); // git (the fake) finalized the removal
			Assert.Null(registry.FindByBranch("owned"));
		} finally {
			if (Directory.Exists(wtPath)) {
				Directory.Delete(wtPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task Remove_OwnedWorktree_GitStillFailsAfterClearing_DeletesRemainingDirectory() {
		var (manager, registry, git) = NewManager();
		string wtPath = Path.Combine(WorktreesDir, "broken-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(wtPath);
		File.WriteAllText(Path.Combine(wtPath, ".git"), "gitdir: nowhere\n");
		File.WriteAllText(Path.Combine(wtPath, "leftover.txt"), "x");
		try {
			git.Worktrees.Add(new GitWorktree { Path = wtPath, Branch = "broken", Head = "b1" });
			registry.Add(new WorktreeRecord { Branch = "broken", Path = wtPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
			// git can't finalize even against the cleared tree (a pre-broken .git linkage): not a transient lock,
			// so no retry — we delete the remaining directory ourselves rather than leak it.
			git.RemoveWorktreeFailures.Enqueue(new GitException("git worktree remove failed (exit 128): fatal: validation failed, cannot remove working tree: '.git' does not exist"));
			var logs = new List<string>();
			manager.Log += logs.Add;

			await manager.RemoveAsync(wtPath, deleteBranch: false, force: true);

			Assert.False(Directory.Exists(wtPath)); // remaining directory deleted directly
			Assert.Null(registry.FindByBranch("broken"));
			Assert.DoesNotContain(logs, l => l.Contains("retrying", StringComparison.Ordinal)); // not retried as a transient lock
			Assert.Contains(logs, l => l.Contains("deleting the remaining directory directly", StringComparison.Ordinal));
		} finally {
			if (Directory.Exists(wtPath)) {
				Directory.Delete(wtPath, recursive: true);
			}
		}
	}

	[Fact]
	public async Task Remove_GitFailure_OutsideWorktreesDir_Propagates_WithoutDeleting() {
		var (manager, registry, git) = NewManager();
		// A worktree git tracks but that lives OUTSIDE the managed worktrees dir (e.g. one a user created by
		// hand). Weavie must not clear or delete a directory it doesn't own — git's failure surfaces instead.
		string externalPath = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "external-" + Guid.NewGuid().ToString("n"));
		git.Worktrees.Add(new GitWorktree { Path = externalPath, Branch = "external", Head = "e1" });
		registry.Add(new WorktreeRecord { Branch = "external", Path = externalPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.RemoveWorktreeFailures.Enqueue(new GitException("git worktree remove failed (exit 128): fatal: 'external' is not a working tree"));

		await Assert.ThrowsAsync<GitException>(() => manager.RemoveAsync(externalPath, deleteBranch: false, force: true));
		Assert.NotNull(registry.FindByBranch("external")); // not dropped — the removal genuinely failed
	}

	[Fact]
	public async Task Remove_GitUntracksButDirectoryRemains_OutsideWorktreesDir_ThrowsOrphan_WithoutDeleting() {
		var (manager, registry, git) = NewManager();
		// git no longer tracks the path, yet a directory remains at it — and it lives OUTSIDE the managed
		// worktrees dir. Weavie must surface it rather than delete a directory it doesn't own.
		string externalPath = Path.Combine(Path.GetTempPath(), "weavie-wt-mgr-tests", "orphan-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(externalPath);
		try {
			registry.Add(new WorktreeRecord { Branch = "orphan", Path = externalPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });

			await Assert.ThrowsAsync<WorktreeOrphanException>(() => manager.RemoveAsync(externalPath, deleteBranch: false, force: true));

			Assert.True(Directory.Exists(externalPath)); // never deleted — it's outside the managed dir
			Assert.NotNull(registry.FindByBranch("orphan")); // registry row not dropped on the surfaced failure
		} finally {
			if (Directory.Exists(externalPath)) {
				Directory.Delete(externalPath, recursive: true);
			}
		}
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

	[Fact]
	public async Task Remove_RunsTeardownWhileWorktreeStillExists() {
		var registry = new WorktreeRegistry(new InMemoryFileSystem(), RegistryPath);
		var git = new FakeGitService { DefaultBranch = "main" };
		git.Worktrees.Add(new GitWorktree { Path = RepoRoot, Branch = "main", Head = "primary" });
		string wtPath = Path.Combine(WorktreesDir, "feature");
		git.Worktrees.Add(new GitWorktree { Path = wtPath, Branch = "feature", Head = "f1" });
		registry.Add(new WorktreeRecord { Branch = "feature", Path = wtPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });

		bool existedAtTeardown = false;
		var provisioner = new RecordingProvisioner(p => existedAtTeardown = git.Worktrees.Any(w => w.Path == p));
		var manager = new WorktreeManager(git, registry, RepoRoot, WorktreesDir, provisioner);

		await manager.RemoveAsync(wtPath, deleteBranch: false, force: false);

		Assert.Equal(wtPath, Assert.Single(provisioner.TeardownPaths));
		Assert.True(existedAtTeardown); // teardown ran before the worktree was removed from git
		Assert.DoesNotContain(git.Worktrees, w => w.Branch == "feature");
	}

	[Fact]
	public async Task Remove_Dirty_WithoutForce_DoesNotRunTeardown() {
		var registry = new WorktreeRegistry(new InMemoryFileSystem(), RegistryPath);
		var git = new FakeGitService { DefaultBranch = "main" };
		git.Worktrees.Add(new GitWorktree { Path = RepoRoot, Branch = "main", Head = "primary" });
		string wipPath = Path.Combine(WorktreesDir, "wip");
		git.Worktrees.Add(new GitWorktree { Path = wipPath, Branch = "wip", Head = "w1" });
		registry.Add(new WorktreeRecord { Branch = "wip", Path = wipPath, BaseRef = "main", CreatedAtUtc = DateTimeOffset.UnixEpoch });
		git.DirtyPaths.Add(wipPath);
		var provisioner = new RecordingProvisioner(onTeardown: null);
		var manager = new WorktreeManager(git, registry, RepoRoot, WorktreesDir, provisioner);

		await Assert.ThrowsAsync<WorktreeDirtyException>(() => manager.RemoveAsync(wipPath, deleteBranch: false, force: false));

		Assert.Empty(provisioner.TeardownPaths); // the dirty guard precedes teardown, so a refused removal runs nothing
	}

	/// <summary>An <see cref="IWorktreeProvisioner"/> that records the paths it was asked to provision.</summary>
	private sealed class RecordingProvisioner : IWorktreeProvisioner {
		private readonly Action<string>? _onTeardown;

		public RecordingProvisioner(Action<string>? onTeardown) {
			_onTeardown = onTeardown;
		}

		public List<string> SetupPaths { get; } = [];

		public List<string> TeardownPaths { get; } = [];

		public Task<WorktreeCommandResult> RunSetupAsync(string worktreePath, CancellationToken ct) {
			SetupPaths.Add(worktreePath);
			return Task.FromResult(new WorktreeCommandResult { Ran = true });
		}

		public Task<WorktreeCommandResult> RunTeardownAsync(string worktreePath, CancellationToken ct) {
			TeardownPaths.Add(worktreePath);
			_onTeardown?.Invoke(worktreePath);
			return Task.FromResult(new WorktreeCommandResult { Ran = true });
		}
	}

	/// <summary>An in-memory <see cref="IGitService"/> with controllable branches, worktrees, dirty paths, and merge state.</summary>
	private sealed class FakeGitService : IGitService {
		public HashSet<string> Branches { get; } = new(StringComparer.Ordinal);

		public List<GitWorktree> Worktrees { get; } = [];

		public HashSet<string> DirtyPaths { get; } = new(StringComparer.Ordinal);

		public HashSet<string> DirtyProbeFailures { get; } = new(StringComparer.Ordinal);

		public HashSet<string> MergedBranches { get; } = new(StringComparer.Ordinal);

		public string? DefaultBranch { get; set; }

		public Task<bool> IsRepositoryAsync(string directory, CancellationToken ct = default) => Task.FromResult(true);

		public Task<string> GetHeadCommitAsync(string directory, CancellationToken ct = default) => Task.FromResult("0000000");

		public Task<string?> GetCurrentBranchAsync(string directory, CancellationToken ct = default) => Task.FromResult(DefaultBranch);

		public Task FetchAsync(string repositoryDirectory, string remote, string refName, CancellationToken ct = default) => Task.CompletedTask;

		public Task<string?> GetRemoteUrlAsync(string repositoryDirectory, string remote, CancellationToken ct = default) => Task.FromResult<string?>(null);

		public Task<string?> MergeBaseAsync(string repositoryDirectory, string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);

		public Task<IReadOnlyList<DiffFileChange>> DiffRefsAsync(string repositoryDirectory, string fromRef, string toRef, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<DiffFileChange>>([]);

		public Task<string?> ResolveCommitAsync(string repositoryDirectory, string reference, CancellationToken ct = default) => Task.FromResult<string?>(null);

		public Task<IReadOnlyList<DiffFileChange>> DiffWorktreeAsync(string repositoryDirectory, string baseRef, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<DiffFileChange>>([]);

		public Task<string> ShowFileAtRefAsync(string repositoryDirectory, string reference, string path, CancellationToken ct = default) => Task.FromResult(string.Empty);

		public Task<bool> BranchExistsAsync(string directory, string branch, CancellationToken ct = default) => Task.FromResult(Branches.Contains(branch));

		public Task<IReadOnlyList<string>> ListBranchesAsync(string directory, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<string>>([.. Branches]);

		public Task<IReadOnlyList<string>> ListRefsAsync(string directory, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<string>>([.. Branches]);

		public Task<string?> ResolveDefaultBranchAsync(string directory, CancellationToken ct = default) => Task.FromResult(DefaultBranch);

		public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken ct = default) =>
			Task.FromResult<IReadOnlyList<GitWorktree>>([.. Worktrees]);

		public Task AddWorktreeAsync(string repositoryDirectory, string worktreePath, string newBranch, string baseRef, CancellationToken ct = default) {
			Branches.Add(newBranch);
			Worktrees.Add(new GitWorktree { Path = worktreePath, Branch = newBranch, Head = "1111111" });
			return Task.CompletedTask;
		}

		public Task AttachWorktreeAsync(string repositoryDirectory, string worktreePath, string branch, CancellationToken ct = default) {
			Worktrees.Add(new GitWorktree { Path = worktreePath, Branch = branch, Head = "2222222" });
			return Task.CompletedTask;
		}

		public Queue<Exception> RemoveWorktreeFailures { get; } = new();

		/// <summary>Observes the on-disk state at the moment `git worktree remove` is invoked (before it acts).</summary>
		public Action<string>? OnRemoveWorktree { get; set; }

		public Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default) {
			if (RemoveWorktreeFailures.Count > 0) {
				return Task.FromException(RemoveWorktreeFailures.Dequeue());
			}

			OnRemoveWorktree?.Invoke(worktreePath);

			// Mirror real git: a successful `git worktree remove` drops its own admin record AND deletes the
			// working-tree directory. (The directory may not exist in pure-logic tests; deletion is best-effort.)
			Worktrees.RemoveAll(w => PathEquals(w.Path, worktreePath));
			if (Directory.Exists(worktreePath)) {
				Directory.Delete(worktreePath, recursive: true);
			}

			return Task.CompletedTask;
		}

		public Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default) {
			if (DirtyProbeFailures.Any(p => PathEquals(p, worktreeDirectory))) {
				return Task.FromException<bool>(new GitException("dirty probe should not run"));
			}

			return Task.FromResult(DirtyPaths.Any(p => PathEquals(p, worktreeDirectory)));
		}

		public Task<WorktreeChangeStatus> GetChangeStateAsync(string worktreeDirectory, CancellationToken ct = default) =>
			Task.FromResult(new WorktreeChangeStatus(
				DirtyPaths.Any(p => PathEquals(p, worktreeDirectory))
					? WorktreeChangeState.Modified
					: WorktreeChangeState.Clean,
				[],
				[]));

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
