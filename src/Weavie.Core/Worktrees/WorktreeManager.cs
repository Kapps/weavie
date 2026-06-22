using Weavie.Core.Git;

namespace Weavie.Core.Worktrees;

/// <summary>
/// Creates, lists, classifies, and removes the git worktrees that back Weavie's per-session work, keeping
/// the persisted <see cref="WorktreeRegistry"/> reconciled with what git actually reports so nothing silently
/// leaks. Every list/reconcile compares the registry against live <c>git worktree list</c> output and reports
/// the drift; removal is guarded against discarding uncommitted work.
/// </summary>
public sealed class WorktreeManager {
	// Worktree paths are compared only for identity (existsInGit, dedup, allocation collisions) and containment
	// (IsWithinWorktreesDir) — never for anything that needs to distinguish two paths by case. Weavie never
	// creates two worktrees whose paths differ only in case, so case-insensitive comparison is always safe; it's
	// also what the filesystems Weavie targets (Windows, macOS) actually do, so a path git reports with different
	// casing than ours (e.g. the primary checkout) still matches. Hence one OS-independent comparer, not an
	// OperatingSystem.IsWindows() branch.
	private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	private readonly IGitService _git;
	private readonly IWorktreeProvisioner _provisioner;
	private readonly string _repositoryRoot;
	private readonly string _worktreesDir;

	/// <summary>
	/// Creates a manager for the repository rooted at <paramref name="repositoryRoot"/>, placing new
	/// worktrees under <paramref name="worktreesDir"/>, tracking them in <paramref name="registry"/>, and
	/// running the lifecycle commands of <paramref name="provisioner"/> around create/remove (pass
	/// <see cref="NullWorktreeProvisioner.Instance"/> when there are none).
	/// </summary>
	public WorktreeManager(
		IGitService git, WorktreeRegistry registry, string repositoryRoot, string worktreesDir, IWorktreeProvisioner provisioner) {
		ArgumentNullException.ThrowIfNull(git);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentException.ThrowIfNullOrEmpty(repositoryRoot);
		ArgumentException.ThrowIfNullOrEmpty(worktreesDir);
		ArgumentNullException.ThrowIfNull(provisioner);
		_git = git;
		Registry = registry;
		_repositoryRoot = repositoryRoot;
		_worktreesDir = worktreesDir;
		_provisioner = provisioner;
	}

	/// <summary>The registry of Weavie-created worktrees (subscribe to its <see cref="WorktreeRegistry.Changed"/>).</summary>
	public WorktreeRegistry Registry { get; }

	/// <summary>Diagnostic log line (e.g. a retry while a transient file lock clears during removal). Optional.</summary>
	public event Action<string>? Log;

	/// <summary>
	/// Creates a worktree on a new branch <paramref name="branch"/> started from <paramref name="baseRef"/>,
	/// records it in the registry, and returns the record. Throws when <paramref name="branch"/> already
	/// exists — git would refuse, and the registry must not point at an ambiguous branch.
	/// </summary>
	public async Task<WorktreeRecord> CreateAsync(string branch, string baseRef, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(branch);
		ArgumentException.ThrowIfNullOrEmpty(baseRef);
		if (await _git.BranchExistsAsync(_repositoryRoot, branch, ct).ConfigureAwait(false)) {
			throw new InvalidOperationException($"Branch '{branch}' already exists; pick a new branch name or open the existing session.");
		}

		string path = AllocatePath(branch);
		await _git.AddWorktreeAsync(_repositoryRoot, path, branch, baseRef, ct).ConfigureAwait(false);
		var record = new WorktreeRecord {
			Branch = branch,
			Path = Normalize(path),
			BaseRef = baseRef,
			CreatedAtUtc = DateTimeOffset.UtcNow,
		};
		Registry.Add(record);
		return record;
	}

	/// <summary>
	/// Creates a worktree checked out on the existing branch <paramref name="branch"/>, records it, and returns
	/// the record. If Weavie already tracks a worktree for this branch, its existing record is returned so callers
	/// reuse it rather than creating a duplicate. Throws when the branch doesn't exist.
	/// </summary>
	public async Task<WorktreeRecord> AttachAsync(string branch, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(branch);
		if (Registry.FindByBranch(branch) is { } existing) {
			return existing;
		}

		if (!await _git.BranchExistsAsync(_repositoryRoot, branch, ct).ConfigureAwait(false)) {
			throw new InvalidOperationException($"Branch '{branch}' doesn't exist; pick an existing branch or create a new one.");
		}

		string path = AllocatePath(branch);
		await _git.AttachWorktreeAsync(_repositoryRoot, path, branch, ct).ConfigureAwait(false);
		var record = new WorktreeRecord {
			Branch = branch,
			Path = Normalize(path),
			// The branch already has a tip; there's no distinct base. Record the branch itself — this value is
			// persisted bookkeeping only, nothing branches on it.
			BaseRef = branch,
			CreatedAtUtc = DateTimeOffset.UtcNow,
		};
		Registry.Add(record);
		return record;
	}

	/// <summary>
	/// Returns a status for every worktree: those Weavie created (<see cref="WorktreeStatus.IsManaged"/>), the
	/// primary checkout, and any worktree present in git but not the registry (so externally-created ones are
	/// still surfaced). Managed entries git no longer knows about appear with <see cref="WorktreeStatus.Exists"/>
	/// false so the caller can prune them.
	/// </summary>
	public async Task<IReadOnlyList<WorktreeStatus>> ListAsync(CancellationToken ct = default) {
		var gitWorktrees = await _git.ListWorktreesAsync(_repositoryRoot, ct).ConfigureAwait(false);
		string? defaultBranch = await _git.ResolveDefaultBranchAsync(_repositoryRoot, ct).ConfigureAwait(false);
		string normalizedRoot = Normalize(_repositoryRoot);

		var result = new List<WorktreeStatus>();
		var seen = new HashSet<string>(PathComparer);
		foreach (var worktree in gitWorktrees) {
			if (worktree.IsBare) {
				continue;
			}

			string normalized = Normalize(worktree.Path);
			seen.Add(normalized);
			var record = Registry.FindByPath(normalized);
			bool isPrimary = PathComparer.Equals(normalized, normalizedRoot);
			bool isDirty = !isPrimary && await _git.HasUncommittedChangesAsync(worktree.Path, ct).ConfigureAwait(false);
			bool isMerged = !isPrimary
				&& worktree.Branch is { } branch
				&& defaultBranch is { } target
				&& !string.Equals(branch, target, StringComparison.Ordinal)
				&& await _git.IsBranchMergedAsync(_repositoryRoot, branch, target, ct).ConfigureAwait(false);

			result.Add(new WorktreeStatus {
				Path = worktree.Path,
				Branch = worktree.Branch,
				BaseRef = record?.BaseRef,
				IsManaged = record is not null,
				IsPrimary = isPrimary,
				Exists = true,
				IsDirty = isDirty,
				IsMerged = isMerged,
				CreatedAtUtc = record?.CreatedAtUtc,
			});
		}

		foreach (var record in Registry.Items) {
			if (seen.Contains(Normalize(record.Path))) {
				continue;
			}

			result.Add(new WorktreeStatus {
				Path = record.Path,
				Branch = record.Branch,
				BaseRef = record.BaseRef,
				IsManaged = true,
				IsPrimary = false,
				Exists = false,
				IsDirty = false,
				IsMerged = false,
				CreatedAtUtc = record.CreatedAtUtc,
			});
		}

		return result;
	}

	/// <summary>
	/// Removes the worktree at <paramref name="path"/> and drops it from the registry. Refuses with
	/// <see cref="WorktreeDirtyException"/> when the worktree has uncommitted changes unless
	/// <paramref name="force"/> is set. When <paramref name="deleteBranch"/> is set the worktree's branch
	/// is deleted too (force-deleted when <paramref name="force"/>). A worktree already gone from git is
	/// pruned and dropped from the registry.
	/// </summary>
	public async Task RemoveAsync(string path, bool deleteBranch, bool force, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		string normalized = Normalize(path);
		var record = Registry.FindByPath(normalized);

		var gitWorktrees = await _git.ListWorktreesAsync(_repositoryRoot, ct).ConfigureAwait(false);
		bool existsInGit = gitWorktrees.Any(w => PathComparer.Equals(Normalize(w.Path), normalized));
		if (existsInGit) {
			if (!force && await _git.HasUncommittedChangesAsync(path, ct).ConfigureAwait(false)) {
				throw new WorktreeDirtyException(path);
			}

			// Teardown while the working tree still exists. Best-effort: a non-zero exit is surfaced by the
			// provisioner but doesn't abort the removal. Runs only after the dirty guard, so a refused removal
			// runs nothing.
			await _provisioner.RunTeardownAsync(path, ct).ConfigureAwait(false);

			if (IsWithinWorktreesDir(normalized)) {
				await RemoveOwnedWorktreeAsync(path, normalized, ct).ConfigureAwait(false);
			} else {
				// A worktree outside our managed dir (e.g. one a user created by hand): let git own removal
				// entirely — never hand-delete files we don't own. A failure surfaces loudly.
				await RemoveWorktreeWithRetryAsync(path, force, ct).ConfigureAwait(false);
			}
		} else if (Directory.Exists(normalized)) {
			// git no longer tracks this path, yet the directory is still on disk — the state an earlier non-atomic
			// `git worktree remove` leaves when a lock makes it drop its own record but fail to unlink the files.
			// git has no record left to finalize (and so no branch claim to clear), so just delete the directory —
			// when it is one we own. An arbitrary path outside the managed worktrees dir is surfaced, not deleted.
			if (!IsWithinWorktreesDir(normalized)) {
				throw new WorktreeOrphanException(normalized);
			}

			Log?.Invoke($"git no longer tracks '{normalized}' but its directory remains; deleting the directory directly");
			await DeleteDirectoryWithRetryAsync(normalized, ct).ConfigureAwait(false);
		}
		// else: git doesn't track it and the directory is already gone — nothing to remove; fall through to drop
		// the stale registry row below.

		if (deleteBranch && record is not null) {
			await _git.DeleteBranchAsync(_repositoryRoot, record.Branch, force, ct).ConfigureAwait(false);
		}

		Registry.Remove(normalized);
	}

	// Attempts before a removal failure is surfaced. On Windows a brief lock (antivirus, the indexer, Explorer,
	// a child process still closing) can make `git worktree remove` fail with "Directory not empty" even after
	// the session's own handles are released. A short bounded retry re-runs git so the lock can clear; the
	// final attempt's exception propagates, so an unresolved failure surfaces loudly.
	private const int MaxRemoveAttempts = 4;
	private static readonly int[] RemoveRetryDelaysMs = [150, 350, 800];
	private static readonly string[] FileLockPhrases =
		["Directory not empty", "being used by another process", "Permission denied", "Access is denied", "Device or resource busy"];

	private async Task RemoveWorktreeWithRetryAsync(string path, bool force, CancellationToken ct) {
		for (int attempt = 1; ; attempt++) {
			try {
				await _git.RemoveWorktreeAsync(_repositoryRoot, path, force, ct).ConfigureAwait(false);
				return;
			} catch (GitException ex) when (attempt < MaxRemoveAttempts && IsTransientFileLock(ex)) {
				int delayMs = RemoveRetryDelaysMs[attempt - 1];
				Log?.Invoke($"removing worktree '{path}' is blocked by a file lock (attempt {attempt} of {MaxRemoveAttempts}): {FirstLine(ex.Message)} — retrying in {delayMs}ms");
				await Task.Delay(delayMs, ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Removes a worktree Weavie owns by first clearing its working tree ourselves — every entry EXCEPT the
	/// <c>.git</c> link, junction-safe — and then letting <c>git worktree remove</c> finalize. Clearing first
	/// means git's single removal runs against an empty tree, so it can't lose the lock race that used to leave a
	/// half-removed directory, and git removes its OWN admin record (no repo-wide prune, no global git op). If git
	/// still can't finalize (a pre-broken <c>.git</c> linkage), the remaining directory is deleted directly so it
	/// never leaks; that failure is logged loudly.
	/// </summary>
	private async Task RemoveOwnedWorktreeAsync(string path, string normalized, CancellationToken ct) {
		await ClearWorktreeContentsExceptGitAsync(normalized, ct).ConfigureAwait(false);

		try {
			// force: the tree was just emptied, so it reads as dirty; the caller already ran the dirty guard.
			await RemoveWorktreeWithRetryAsync(path, force: true, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Log?.Invoke($"git worktree remove still failed after clearing '{normalized}' ({FirstLine(ex.Message)}); deleting the remaining directory directly");
			await DeleteDirectoryWithRetryAsync(normalized, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Deletes every entry inside <paramref name="dir"/> EXCEPT its <c>.git</c> link (junction-safe), with the
	/// same bounded retry as a git removal so a brief Windows lock can clear. Keeping <c>.git</c> leaves the path
	/// recognizable to <c>git worktree remove</c> while stripping the working tree it would otherwise have to
	/// unlink (and lock-race over).
	/// </summary>
	private async Task ClearWorktreeContentsExceptGitAsync(string dir, CancellationToken ct) {
		for (int attempt = 1; ; attempt++) {
			try {
				var root = new DirectoryInfo(dir);
				if (root.Exists) {
					foreach (var entry in root.EnumerateFileSystemInfos()) {
						if (string.Equals(entry.Name, ".git", StringComparison.Ordinal)) {
							continue; // keep the worktree's git linkage so `git worktree remove` still recognizes it
						}

						DeleteEntryNoFollow(entry);
					}
				}

				return;
			} catch (Exception ex) when (attempt < MaxRemoveAttempts && ex is IOException or UnauthorizedAccessException) {
				int delayMs = RemoveRetryDelaysMs[attempt - 1];
				Log?.Invoke($"clearing worktree '{dir}' is blocked (attempt {attempt} of {MaxRemoveAttempts}): {FirstLine(ex.Message)} — retrying in {delayMs}ms");
				await Task.Delay(delayMs, ct).ConfigureAwait(false);
			}
		}
	}

	private async Task DeleteDirectoryWithRetryAsync(string dir, CancellationToken ct) {
		string normalized = Normalize(dir);
		// Defense in depth for a destructive op: callers already guard, but never delete outside the worktrees dir.
		if (!IsWithinWorktreesDir(normalized)) {
			throw new InvalidOperationException(
				$"Refusing to delete '{normalized}': it is not inside the managed worktrees directory '{_worktreesDir}'.");
		}

		for (int attempt = 1; ; attempt++) {
			try {
				var root = new DirectoryInfo(normalized);
				if (root.Exists) {
					DeleteEntryNoFollow(root);
				}

				return;
			} catch (Exception ex) when (attempt < MaxRemoveAttempts && ex is IOException or UnauthorizedAccessException) {
				int delayMs = RemoveRetryDelaysMs[attempt - 1];
				Log?.Invoke($"deleting worktree directory '{normalized}' is blocked (attempt {attempt} of {MaxRemoveAttempts}): {FirstLine(ex.Message)} — retrying in {delayMs}ms");
				await Task.Delay(delayMs, ct).ConfigureAwait(false);
			}
		}
	}

	// The junction-safe recursive delete primitive. NEVER recurses through a reparse point (junction/symlink):
	// such an entry is unlinked in place, leaving its target untouched. This is the critical guard — a worktree
	// commonly holds a node_modules junction into the primary checkout, and a naive recursive delete that followed
	// it would wipe the primary's files. Read-only files (git packs its object store read-only) are cleared first
	// so Delete doesn't fail with "Access is denied".
	private static void DeleteEntryNoFollow(FileSystemInfo entry) {
		if (IsLink(entry)) {
			// A junction/symlink (directory or file): unlink the entry in place. Delete() removes the reparse point
			// without touching whatever it points at — never recurse into it.
			entry.Delete();
			return;
		}

		if (entry is DirectoryInfo dir) {
			foreach (var child in dir.EnumerateFileSystemInfos()) {
				DeleteEntryNoFollow(child);
			}

			dir.Delete(recursive: false);
			return;
		}

		if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) {
			entry.Attributes &= ~FileAttributes.ReadOnly;
		}

		entry.Delete();
	}

	// A junction or symbolic link. LinkTarget catches symlinks/junctions on every OS; the ReparsePoint attribute
	// is the Windows belt-and-suspenders (a junction always carries it).
	private static bool IsLink(FileSystemInfo entry) =>
		entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

	// True when <paramref name="normalizedPath"/> sits strictly inside the managed worktrees dir — never the dir
	// itself, never outside it. The containment guard for manual deletion: Weavie only ever places worktrees here
	// (see AllocatePath), so a path inside it that git can't remove is safe to delete directly.
	private bool IsWithinWorktreesDir(string normalizedPath) {
		string root = Normalize(_worktreesDir);
		if (PathComparer.Equals(normalizedPath, root)) {
			return false;
		}

		return normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	// Whether git's failure is a transient OS file lock (worth re-running git) rather than a real error. A
	// still-open handle on Windows surfaces as one of these phrases; anything else is surfaced, not retried.
	private static bool IsTransientFileLock(GitException ex) =>
		FileLockPhrases.Any(p => ex.Message.Contains(p, StringComparison.OrdinalIgnoreCase));

	private static string FirstLine(string message) {
		int nl = message.IndexOfAny(['\r', '\n']);
		return nl < 0 ? message : message[..nl];
	}

	/// <summary>
	/// Drops registry rows whose worktree git no longer reports and returns a report plus the post-reconcile
	/// statuses. Never deletes a worktree that still exists, and never runs a repo-wide
	/// <c>git worktree prune</c> (which could drop an unrelated worktree's record), so nothing with real work
	/// in it is touched.
	/// </summary>
	public async Task<WorktreeReconcileReport> ReconcileAsync(CancellationToken ct = default) {
		var statuses = await ListAsync(ct).ConfigureAwait(false);

		int pruned = 0;
		foreach (var status in statuses) {
			if (status.IsManaged && !status.Exists) {
				Registry.Remove(Normalize(status.Path));
				pruned++;
			}
		}

		int untracked = statuses.Count(s => s.IsUntracked);
		var after = pruned > 0 ? await ListAsync(ct).ConfigureAwait(false) : statuses;
		return new WorktreeReconcileReport {
			OrphansPruned = pruned,
			Untracked = untracked,
			Statuses = after,
		};
	}

	private string AllocatePath(string branch) {
		string slug = Slugify(branch);
		var taken = new HashSet<string>(Registry.Items.Select(r => Normalize(r.Path)), PathComparer);
		string candidate = Path.Combine(_worktreesDir, slug);
		int suffix = 2;
		while (taken.Contains(Normalize(candidate)) || Directory.Exists(candidate)) {
			candidate = Path.Combine(_worktreesDir, $"{slug}-{suffix}");
			suffix++;
		}

		return candidate;
	}

	private static string Slugify(string branch) {
		char[] chars = [.. branch.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')];
		string slug = new string(chars).Trim('-', '.');
		return slug.Length == 0 ? "session" : slug;
	}

	private static string Normalize(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
