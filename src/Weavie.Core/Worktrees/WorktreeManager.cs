using Weavie.Core.Git;

namespace Weavie.Core.Worktrees;

/// <summary>
/// Creates, lists, classifies, and removes the git worktrees that back Weavie's per-session work, and —
/// crucially — keeps the persisted <see cref="WorktreeRegistry"/> reconciled with what git actually
/// reports, so a crash, an external <c>git worktree remove</c>, or a worktree Weavie never created can
/// never silently leak. Every list/reconcile compares the registry against live <c>git worktree list</c>
/// output and reports the drift; removal is guarded against discarding uncommitted work.
/// </summary>
public sealed class WorktreeManager {
	private static readonly StringComparer PathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

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
	/// Reconciles the registry against live <c>git worktree list</c> output and returns a status for every
	/// worktree: those Weavie created (<see cref="WorktreeStatus.IsManaged"/>), the primary checkout, and
	/// any worktree present in git but not the registry (so externally-created or registry-lost worktrees
	/// are still surfaced). Managed entries git no longer knows about appear with
	/// <see cref="WorktreeStatus.Exists"/> false so the caller can prune them.
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

			// Run the teardown command while the working tree still exists, then remove it. Teardown is
			// best-effort cleanup: a non-zero exit is surfaced by the provisioner but does not abort the
			// removal the user asked for. Only after passing the dirty guard, so a refused removal runs nothing.
			await _provisioner.RunTeardownAsync(path, ct).ConfigureAwait(false);
			await RemoveWorktreeWithRetryAsync(path, force, ct).ConfigureAwait(false);
		} else {
			// The working directory is already gone from git's point of view; clean up its admin entry.
			await _git.PruneWorktreesAsync(_repositoryRoot, ct).ConfigureAwait(false);
		}

		if (deleteBranch && record is not null) {
			await _git.DeleteBranchAsync(_repositoryRoot, record.Branch, force, ct).ConfigureAwait(false);
		}

		Registry.Remove(normalized);
	}

	// Attempts before a removal failure is surfaced. On Windows a brief lock — antivirus, the search indexer,
	// Explorer, or a child process still closing — can make `git worktree remove` fail with "Directory not
	// empty" even once the session's own handles are released. A short bounded retry simply re-runs git (git
	// stays the one removing the worktree) so the lock can clear. Each retry is logged; the final attempt's
	// exception propagates, so an unresolved failure surfaces loudly rather than being papered over.
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

	// Whether git's failure is a transient OS file lock (worth re-running git) rather than a real error. A
	// still-open handle on Windows surfaces as one of these phrases; anything else — including "Filename too
	// long" — is not transient and is surfaced rather than retried.
	private static bool IsTransientFileLock(GitException ex) =>
		FileLockPhrases.Any(p => ex.Message.Contains(p, StringComparison.OrdinalIgnoreCase));

	private static string FirstLine(string message) {
		int nl = message.IndexOfAny(['\r', '\n']);
		return nl < 0 ? message : message[..nl];
	}

	/// <summary>
	/// Prunes stale git worktree admin entries, drops registry rows whose worktree no longer exists, and
	/// returns a report plus the post-reconcile statuses. Never deletes a worktree that still exists — it
	/// only reconciles bookkeeping, so nothing with real work in it is touched.
	/// </summary>
	public async Task<WorktreeReconcileReport> ReconcileAsync(CancellationToken ct = default) {
		await _git.PruneWorktreesAsync(_repositoryRoot, ct).ConfigureAwait(false);
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
