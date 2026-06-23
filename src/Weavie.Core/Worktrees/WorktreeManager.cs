using Weavie.Core.Git;

namespace Weavie.Core.Worktrees;

/// <summary>
/// Creates, lists, classifies, and removes the git worktrees backing Weavie's per-session work, keeping the
/// persisted <see cref="WorktreeRegistry"/> reconciled against live <c>git worktree list</c> output so nothing
/// leaks; removal is guarded against discarding uncommitted work.
/// </summary>
public sealed class WorktreeManager {
	// One OS-independent case-insensitive comparer: paths are compared only for identity and containment, Weavie
	// never makes two worktrees differing only in case, and it matches git reporting the primary checkout's casing.
	private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	private readonly IGitService _git;
	private readonly IWorktreeProvisioner _provisioner;
	private readonly string _repositoryRoot;
	private readonly string _worktreesDir;

	/// <summary>
	/// Creates a manager for the repo at <paramref name="repositoryRoot"/>, placing new worktrees under
	/// <paramref name="worktreesDir"/>, tracking them in <paramref name="registry"/>, and running
	/// <paramref name="provisioner"/>'s lifecycle commands around create/remove.
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
	/// records it, and returns the record. Throws when <paramref name="branch"/> already exists.
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
	/// Creates a worktree on the existing branch <paramref name="branch"/>, records it, and returns the record.
	/// Returns the existing record if Weavie already tracks this branch, so callers don't duplicate it. Throws
	/// when the branch doesn't exist.
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
			// No distinct base; record the branch itself. Persisted bookkeeping only, nothing branches on it.
			BaseRef = branch,
			CreatedAtUtc = DateTimeOffset.UtcNow,
		};
		Registry.Add(record);
		return record;
	}

	/// <summary>
	/// Returns a status for every worktree — Weavie-managed, primary checkout, and present-in-git-but-untracked.
	/// Managed entries git no longer knows about appear with <see cref="WorktreeStatus.Exists"/> false so the
	/// caller can prune them.
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
	/// <see cref="WorktreeDirtyException"/> on uncommitted changes unless <paramref name="force"/> is set.
	/// <paramref name="deleteBranch"/> also deletes the branch (force-deleted when <paramref name="force"/>).
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

			// Teardown while the tree still exists; best-effort (a non-zero exit is surfaced, not aborting), past the dirty guard.
			await _provisioner.RunTeardownAsync(path, ct).ConfigureAwait(false);

			if (IsWithinWorktreesDir(normalized)) {
				await RemoveOwnedWorktreeAsync(path, normalized, ct).ConfigureAwait(false);
			} else {
				// Outside our managed dir: let git own removal entirely, never hand-deleting files we don't own.
				await RemoveWorktreeWithRetryAsync(path, force, ct).ConfigureAwait(false);
			}
		} else if (Directory.Exists(normalized)) {
			// git dropped its record (a non-atomic `git worktree remove` that failed to unlink) but the directory
			// remains. Nothing left for git to finalize, so delete the directory directly when we own it; an
			// arbitrary path outside the managed worktrees dir is surfaced, not deleted.
			if (!IsWithinWorktreesDir(normalized)) {
				throw new WorktreeOrphanException(normalized);
			}

			Log?.Invoke($"git no longer tracks '{normalized}' but its directory remains; deleting the directory directly");
			await DeleteDirectoryWithRetryAsync(normalized, ct).ConfigureAwait(false);
		}
		// else: untracked and already gone — nothing to remove; fall through to drop the stale registry row.

		if (deleteBranch && record is not null) {
			await _git.DeleteBranchAsync(_repositoryRoot, record.Branch, force, ct).ConfigureAwait(false);
		}

		Registry.Remove(normalized);
	}

	// A brief Windows file lock (antivirus, indexer, Explorer) can fail `git worktree remove` with "Directory not
	// empty" after our handles are released; bounded retry lets the lock clear, then the final exception propagates.
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
	/// Removes a Weavie-owned worktree by clearing its tree ourselves (every entry except the junction-safe
	/// <c>.git</c> link) so git's removal runs against an empty tree, dodging the lock race, then removing only
	/// git's own admin record. If git still can't finalize, the remaining directory is deleted directly so it never leaks.
	/// </summary>
	private async Task RemoveOwnedWorktreeAsync(string path, string normalized, CancellationToken ct) {
		await ClearWorktreeContentsExceptGitAsync(normalized, ct).ConfigureAwait(false);

		try {
			// force: the just-emptied tree reads as dirty, but the caller already ran the dirty guard.
			await RemoveWorktreeWithRetryAsync(path, force: true, ct).ConfigureAwait(false);
		} catch (GitException ex) {
			Log?.Invoke($"git worktree remove still failed after clearing '{normalized}' ({FirstLine(ex.Message)}); deleting the remaining directory directly");
			await DeleteDirectoryWithRetryAsync(normalized, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Deletes every entry inside <paramref name="dir"/> except its junction-safe <c>.git</c> link, with bounded
	/// retry for a brief Windows lock. Keeping <c>.git</c> leaves the path recognizable to <c>git worktree
	/// remove</c> while stripping the tree it would otherwise have to unlink and lock-race over.
	/// </summary>
	private async Task ClearWorktreeContentsExceptGitAsync(string dir, CancellationToken ct) {
		for (int attempt = 1; ; attempt++) {
			try {
				var root = new DirectoryInfo(dir);
				if (root.Exists) {
					foreach (var entry in root.EnumerateFileSystemInfos()) {
						if (string.Equals(entry.Name, ".git", StringComparison.Ordinal)) {
							continue; // keep .git so `git worktree remove` still recognizes the path
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

	// Junction-safe recursive delete. NEVER recurses through a reparse point (junction/symlink) — it's unlinked in
	// place, leaving its target untouched: a worktree's node_modules junction into the primary must not wipe the
	// primary. Read-only files (git's read-only object store) are cleared first so Delete doesn't fail.
	private static void DeleteEntryNoFollow(FileSystemInfo entry) {
		if (IsLink(entry)) {
			// Junction/symlink: Delete() unlinks the reparse point in place without touching its target.
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

	// LinkTarget catches symlinks/junctions on every OS; the ReparsePoint attribute is the Windows belt-and-suspenders.
	private static bool IsLink(FileSystemInfo entry) =>
		entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

	// True when the path sits strictly inside the managed worktrees dir. The containment guard for manual deletion:
	// Weavie only places worktrees here, so a path inside it that git can't remove is safe to delete directly.
	private bool IsWithinWorktreesDir(string normalizedPath) {
		string root = Normalize(_worktreesDir);
		if (PathComparer.Equals(normalizedPath, root)) {
			return false;
		}

		return normalizedPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	// Whether git's failure is a transient OS file lock (worth retrying) rather than a real error; anything else is surfaced.
	private static bool IsTransientFileLock(GitException ex) =>
		FileLockPhrases.Any(p => ex.Message.Contains(p, StringComparison.OrdinalIgnoreCase));

	private static string FirstLine(string message) {
		int nl = message.IndexOfAny(['\r', '\n']);
		return nl < 0 ? message : message[..nl];
	}

	/// <summary>
	/// Drops registry rows whose worktree git no longer reports and returns a report plus post-reconcile statuses.
	/// Never deletes a worktree that still exists, nor runs a repo-wide <c>git worktree prune</c> (which could drop
	/// an unrelated worktree's record), so nothing with real work is touched.
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
