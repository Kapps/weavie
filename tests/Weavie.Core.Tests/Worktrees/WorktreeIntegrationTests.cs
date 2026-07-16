using System.Diagnostics;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Worktrees;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// End-to-end tests against a real temporary git repository (<see cref="GitService"/> +
/// <see cref="WorktreeManager"/> + <see cref="WorktreeRegistry"/>): create/list/remove round-trips, the
/// dirty-removal guard, externally-removed worktrees surfaced as orphans and pruned by reconcile, and
/// externally-created worktrees surfaced as untracked. Requires <c>git</c> on PATH.
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
		// A fresh branch sits at main's commit, so it is already an ancestor of main.
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

		// Fresh worktree off a commit: clean, no untracked files.
		var clean = await _git.GetChangeStateAsync(record.Path);
		Assert.Equal(WorktreeChangeState.Clean, clean.State);
		Assert.Empty(clean.TrackedFiles);
		Assert.Empty(clean.UntrackedFiles);

		// A file git doesn't know about: untracked-only, and named in the list.
		File.WriteAllText(Path.Combine(record.Path, "temp.txt"), "scratch\n");
		var untracked = await _git.GetChangeStateAsync(record.Path);
		Assert.Equal(WorktreeChangeState.UntrackedOnly, untracked.State);
		Assert.Empty(untracked.TrackedFiles);
		Assert.Equal(["temp.txt"], untracked.UntrackedFiles);

		// Editing a tracked file is a tracked change even with the untracked file present — the stronger
		// classification wins, and the untracked file is still listed.
		File.WriteAllText(Path.Combine(record.Path, "readme.txt"), "edited\n");
		var modified = await _git.GetChangeStateAsync(record.Path);
		Assert.Equal(WorktreeChangeState.Modified, modified.State);
		Assert.Equal(["readme.txt"], modified.TrackedFiles);
		Assert.Equal(["temp.txt"], modified.UntrackedFiles);
	}

	[Fact]
	public async Task GetChangeState_ListsStagedAddDeleteAndRenamePaths() {
		File.WriteAllText(Path.Combine(_repo, "delete-me.txt"), "delete\n");
		File.WriteAllText(Path.Combine(_repo, "rename-me.txt"), "rename\n");
		RunGit(_repo, "add", "-A");
		RunGit(_repo, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "commit", "-m", "more files");
		var record = await NewManager().CreateAsync("staged-changes", "main");

		File.WriteAllText(Path.Combine(record.Path, "readme.txt"), "edited\n");
		File.Delete(Path.Combine(record.Path, "delete-me.txt"));
		File.Move(Path.Combine(record.Path, "rename-me.txt"), Path.Combine(record.Path, "renamed.txt"));
		File.WriteAllText(Path.Combine(record.Path, "staged-new.txt"), "new\n");
		RunGit(record.Path, "add", "-A");

		var status = await _git.GetChangeStateAsync(record.Path);

		Assert.Equal(WorktreeChangeState.Modified, status.State);
		Assert.Equal(["delete-me.txt", "readme.txt", "renamed.txt", "staged-new.txt"], status.TrackedFiles);
		Assert.Empty(status.UntrackedFiles);
	}

	[Fact]
	public async Task ExternallyRemovedWorktree_SurfacedAsOrphan_AndReconciled() {
		var manager = NewManager();
		var record = await manager.CreateAsync("ghost", "main");

		// Remove out of band: git forgets it, the registry still has it.
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
		// HEAD attaches to the existing branch itself so commits land on it, not a fresh branch.
		Assert.Equal("existing", await _git.GetCurrentBranchAsync(record.Path));
		Assert.NotNull(manager.Registry.FindByBranch("existing"));
	}

	[Fact]
	public async Task Attach_BranchCheckedOutElsewhere_Throws() {
		var manager = NewManager();
		// 'main' is checked out in the primary repo, so a second worktree can't attach to it.
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
	public async Task ListRefs_IncludesRemoteTrackingBranches_MinusRemoteHead() {
		RunGit(_repo, "branch", "alpha", "main");
		string head = RunGitOut(_repo, "rev-parse", "main").Trim();
		// A remote-tracking branch + its symbolic HEAD, without a live remote — the state a fetch leaves behind.
		RunGit(_repo, "update-ref", "refs/remotes/origin/master", head);
		RunGit(_repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/master");
		// A real remote branch whose last segment is literally HEAD — a diff target, not a symbolic alias.
		RunGit(_repo, "update-ref", "refs/remotes/origin/feature/HEAD", head);

		var refs = await _git.ListRefsAsync(_repo);

		Assert.Contains("main", refs);
		Assert.Contains("alpha", refs);
		Assert.Contains("origin/master", refs);
		// A deeper "…/HEAD" is a real branch, kept; only the remote's own symbolic HEAD alias is dropped.
		Assert.Contains("origin/feature/HEAD", refs);
		Assert.DoesNotContain("origin", refs);
		Assert.DoesNotContain("origin/HEAD", refs);
		// Local branches sort ahead of remotes, so the typeahead is local-first.
		Assert.True(refs.ToList().IndexOf("main") < refs.ToList().IndexOf("origin/master"));
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

	[Fact]
	public async Task HalfRemovedWorktree_DirectoryRemains_DeletedDirectly() {
		var manager = NewManager();
		var record = await manager.CreateAsync("half", "main");

		// Simulate a lock-induced half-removal: git's record is gone but the directory remains on disk with
		// leftover files (the state a Windows file lock leaves behind), and the registry row survives.
		RunGit(_repo, "worktree", "remove", "--force", record.Path);
		Directory.CreateDirectory(record.Path);
		File.WriteAllText(Path.Combine(record.Path, "leftover.txt"), "locked\n");

		// git can't finish this, so RemoveAsync deletes the (owned) directory directly rather than leaking it.
		await manager.RemoveAsync(record.Path, deleteBranch: false, force: false);

		Assert.False(Directory.Exists(record.Path));
		Assert.Null(manager.Registry.FindByBranch("half"));
	}

	[Fact]
	public async Task Remove_ClearsContentsThenGitFinalizes_RecordGone_BranchKept_NoPrune() {
		var manager = NewManager();
		var record = await manager.CreateAsync("keepbranch", "main");
		// Extra working-tree content (incl. a nested dir) that our clear step must remove before git finalizes.
		File.WriteAllText(Path.Combine(record.Path, "scratch.txt"), "wip\n");
		Directory.CreateDirectory(Path.Combine(record.Path, "nested"));
		File.WriteAllText(Path.Combine(record.Path, "nested", "deep.txt"), "deep\n");

		await manager.RemoveAsync(record.Path, deleteBranch: false, force: true);

		Assert.False(Directory.Exists(record.Path));
		Assert.Null(manager.Registry.FindByBranch("keepbranch"));
		// git removed its OWN admin record (no repo-wide prune): the worktree no longer appears...
		Assert.DoesNotContain(await manager.ListAsync(), s => s.Branch == "keepbranch");
		// ...and because deleteBranch was false, the branch itself survives — its committed work is intact.
		Assert.True(await _git.BranchExistsAsync(_repo, "keepbranch"));
	}

	[Fact]
	public async Task ManualDeletion_DoesNotFollowJunctions_PreservingTheirTargets() {
		if (!OperatingSystem.IsWindows()) {
			return; // junctions are a Windows construct; the no-follow guard matters (and is testable) there.
		}

		var manager = NewManager();
		var record = await manager.CreateAsync("withlink", "main");

		// A directory OUTSIDE the worktree whose contents MUST survive the worktree's deletion — this stands in
		// for the primary checkout that a worktree's node_modules is junctioned into during live testing.
		string target = Path.Combine(_root, "precious");
		Directory.CreateDirectory(target);
		File.WriteAllText(Path.Combine(target, "keep.txt"), "do not delete\n");

		// A junction inside the worktree pointing at it. Deleting the worktree clears its contents (this junction
		// included) before git finalizes — the clear step must unlink the junction in place, never recurse into it.
		string junction = Path.Combine(record.Path, "linked");
		RunCmd(record.Path, "mklink", "/J", junction, target);
		Assert.True(File.Exists(Path.Combine(junction, "keep.txt"))); // the junction resolves to the target

		await manager.RemoveAsync(record.Path, deleteBranch: false, force: true);

		Assert.False(Directory.Exists(record.Path)); // worktree (and the junction entry) gone
		Assert.True(Directory.Exists(target)); // the junction's TARGET survived — we never recursed through it
		Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
	}

	private static void RunGit(string workingDirectory, params string[] args) => RunGitOut(workingDirectory, args);

	private static string RunGitOut(string workingDirectory, params string[] args) {
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
		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {error.Trim()}");
		}

		return output;
	}

	// Runs a cmd.exe builtin (e.g. `mklink /J` to create a junction, which needs no elevation). Windows-only.
	private static void RunCmd(string workingDirectory, params string[] args) {
		var info = new ProcessStartInfo {
			FileName = "cmd.exe",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true,
		};
		info.ArgumentList.Add("/c");
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info)!;
		_ = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"cmd {string.Join(' ', args)} failed (exit {process.ExitCode}): {error.Trim()}");
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
