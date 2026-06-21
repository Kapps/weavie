using System.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// End-to-end tests against a real temporary git repository: <see cref="GitService"/> +
/// <see cref="WorktreeManager"/> + <see cref="WorktreeRegistry"/>. These prove the no-leak behavior on
/// real git — create/list/remove round-trips, the dirty-removal guard, externally-removed worktrees
/// surfaced as orphans and pruned by reconcile, and externally-created worktrees surfaced as untracked.
/// Requires <c>git</c> on PATH.
/// </summary>
public sealed class WorktreeIntegrationTests : IDisposable {
	private readonly string _root;
	private readonly string _repo;
	private readonly GitService _git = new();

	public WorktreeIntegrationTests() {
		_root = Path.Combine(Path.GetTempPath(), "weavie-git-it-" + Guid.NewGuid().ToString("n"));
		_repo = Path.Combine(_root, "repo");
		Directory.CreateDirectory(_repo);
		RunGit(_repo, "init", "-b", "main");
		File.WriteAllText(Path.Combine(_repo, "readme.txt"), "hello\n");
		RunGit(_repo, "add", "-A");
		RunGit(_repo, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "-m", "initial");
	}

	private WorktreeManager NewManager() {
		var registry = new WorktreeRegistry(new LocalFileSystem(), Path.Combine(_root, "worktrees.json"));
		return new WorktreeManager(_git, registry, _repo, Path.Combine(_root, "worktrees"), NullWorktreeProvisioner.Instance);
	}

	[Fact]
	public async Task CreateListRemove_RoundTrips() {
		var manager = NewManager();

		var record = await manager.CreateAsync("feature", "main");
		Assert.True(Directory.Exists(record.Path));

		var list = await manager.ListAsync();
		Assert.Contains(list, s => s.IsPrimary);
		var feature = list.Single(s => s.Branch == "feature");
		Assert.True(feature.IsManaged);
		Assert.True(feature.Exists);
		Assert.False(feature.IsDirty);
		// A fresh branch sits at main's commit, so it is already an ancestor of main (nothing to merge).
		Assert.True(feature.IsMerged);
		Assert.True(feature.IsSafeToRemove);

		await manager.RemoveAsync(record.Path, deleteBranch: true, force: false);

		Assert.False(Directory.Exists(record.Path));
		Assert.Empty(manager.Registry.Items);
		Assert.False(await _git.BranchExistsAsync(_repo, "feature"));
	}

	[Fact]
	public async Task DirtyWorktree_RemovalGuarded() {
		var manager = NewManager();
		var record = await manager.CreateAsync("wip", "main");
		File.WriteAllText(Path.Combine(record.Path, "scratch.txt"), "uncommitted\n");

		Assert.True(await _git.HasUncommittedChangesAsync(record.Path));
		await Assert.ThrowsAsync<WorktreeDirtyException>(() => manager.RemoveAsync(record.Path, deleteBranch: false, force: false));
		Assert.True(Directory.Exists(record.Path));

		await manager.RemoveAsync(record.Path, deleteBranch: true, force: true);
		Assert.False(Directory.Exists(record.Path));
		Assert.Empty(manager.Registry.Items);
	}

	[Fact]
	public async Task GetChangeState_ClassifiesCleanUntrackedAndModified() {
		var manager = NewManager();
		var record = await manager.CreateAsync("changes", "main");

		// Fresh worktree off a commit: clean.
		Assert.Equal(WorktreeChangeState.Clean, await _git.GetChangeStateAsync(record.Path));

		// A new file that git doesn't know about: untracked-only.
		File.WriteAllText(Path.Combine(record.Path, "temp.txt"), "scratch\n");
		Assert.Equal(WorktreeChangeState.UntrackedOnly, await _git.GetChangeStateAsync(record.Path));

		// Editing a tracked file (readme.txt came from the initial commit) is a tracked change, even with the
		// untracked file still present — the stronger classification wins.
		File.WriteAllText(Path.Combine(record.Path, "readme.txt"), "edited\n");
		Assert.Equal(WorktreeChangeState.Modified, await _git.GetChangeStateAsync(record.Path));
	}

	[Fact]
	public async Task ExternallyRemovedWorktree_SurfacedAsOrphan_AndReconciled() {
		var manager = NewManager();
		var record = await manager.CreateAsync("ghost", "main");

		// Remove the worktree out of band (not through the manager): git forgets it, the registry still has it.
		RunGit(_repo, "worktree", "remove", record.Path);

		var list = await manager.ListAsync();
		var ghost = list.Single(s => s.Branch == "ghost");
		Assert.True(ghost.IsOrphan);
		Assert.False(ghost.Exists);

		var report = await manager.ReconcileAsync();
		Assert.True(report.OrphansPruned >= 1);
		Assert.Null(manager.Registry.FindByBranch("ghost"));
	}

	[Fact]
	public async Task Attach_ExistingBranch_ChecksOutThatBranch() {
		var manager = NewManager();
		// A branch that exists but isn't checked out anywhere.
		RunGit(_repo, "branch", "existing", "main");

		var record = await manager.AttachAsync("existing");

		Assert.Equal("existing", record.Branch);
		Assert.True(Directory.Exists(record.Path));
		// HEAD is attached to the existing branch itself (so commits land on it), not a fresh branch.
		Assert.Equal("existing", await _git.GetCurrentBranchAsync(record.Path));
		Assert.NotNull(manager.Registry.FindByBranch("existing"));
	}

	[Fact]
	public async Task Attach_BranchCheckedOutElsewhere_Throws() {
		var manager = NewManager();
		// 'main' is already checked out in the primary repo, so a second worktree can't attach to it.
		await Assert.ThrowsAsync<GitException>(() => manager.AttachAsync("main"));
	}

	[Fact]
	public async Task ListBranches_ReturnsLocalBranches() {
		RunGit(_repo, "branch", "alpha", "main");
		RunGit(_repo, "branch", "beta", "main");

		var branches = await _git.ListBranchesAsync(_repo);

		Assert.Contains("main", branches);
		Assert.Contains("alpha", branches);
		Assert.Contains("beta", branches);
	}

	[Fact]
	public async Task ExternallyCreatedWorktree_SurfacedAsUntracked() {
		var manager = NewManager();
		string manualPath = Path.Combine(_root, "manual");
		RunGit(_repo, "worktree", "add", "-b", "manual", manualPath, "main");

		var list = await manager.ListAsync();
		var manual = list.Single(s => s.Branch == "manual");
		Assert.True(manual.IsUntracked);
		Assert.False(manual.IsManaged);
	}

	private static void RunGit(string workingDirectory, params string[] args) {
		var info = new ProcessStartInfo {
			FileName = "git",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!;
		_ = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {error.Trim()}");
		}
	}

	public void Dispose() {
		try {
			DeleteDirectoryRobust(_root);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Best-effort temp cleanup; leftover temp dirs are harmless.
		}
	}

	private static void DeleteDirectoryRobust(string directory) {
		if (!Directory.Exists(directory)) {
			return;
		}

		foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
			try {
				File.SetAttributes(file, FileAttributes.Normal);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				// Ignore; the recursive delete below will surface anything fatal.
			}
		}

		Directory.Delete(directory, recursive: true);
	}
}
