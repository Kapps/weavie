using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Git;

/// <summary>
/// <see cref="IGitService"/> backed by the <c>git</c> executable on <c>PATH</c>. Each call spawns a
/// short-lived <c>git</c> process (a transient one-shot helper, exempt from <c>ProcessSupervisor</c>),
/// captures stdout/stderr, and throws <see cref="GitException"/> when a command that must succeed exits
/// non-zero or when git can't be started.
/// </summary>
public sealed class GitService : IGitService {
	private const string HeadsPrefix = "refs/heads/";

	/// <inheritdoc/>
	public async Task<bool> IsRepositoryAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var result = await RunAsync(directory, ["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
		return result.ExitCode == 0 && result.StdOut.Trim() == "true";
	}

	/// <inheritdoc/>
	public async Task<string> GetHeadCommitAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var result = await RunCheckedAsync(directory, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
		return result.StdOut.Trim();
	}

	/// <inheritdoc/>
	public async Task<string?> GetCurrentBranchAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var result = await RunCheckedAsync(directory, ["rev-parse", "--abbrev-ref", "HEAD"], ct).ConfigureAwait(false);
		string branch = result.StdOut.Trim();
		return branch is "HEAD" or "" ? null : branch;
	}

	/// <inheritdoc/>
	public async Task<bool> BranchExistsAsync(string directory, string branch, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		var result = await RunAsync(directory, ["rev-parse", "--verify", "--quiet", HeadsPrefix + branch], ct).ConfigureAwait(false);
		return result.ExitCode == 0;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<string>> ListBranchesAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var result = await RunCheckedAsync(directory, ["for-each-ref", "--format=%(refname:short)", "refs/heads"], ct).ConfigureAwait(false);
		return [.. result.StdOut.Replace("\r", "", StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
	}

	/// <inheritdoc/>
	public async Task<string?> ResolveDefaultBranchAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var origin = await RunAsync(directory, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct).ConfigureAwait(false);
		if (origin.ExitCode == 0) {
			string head = origin.StdOut.Trim();
			int slash = head.IndexOf('/');
			string name = slash >= 0 ? head[(slash + 1)..] : head;
			if (name.Length > 0) {
				return name;
			}
		}

		string[] candidates = ["main", "master"];
		foreach (string candidate in candidates) {
			if (await BranchExistsAsync(directory, candidate, ct).ConfigureAwait(false)) {
				return candidate;
			}
		}

		return null;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var result = await RunCheckedAsync(directory, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
		return ParsePorcelainList(result.StdOut);
	}

	/// <inheritdoc/>
	public async Task AddWorktreeAsync(string repositoryDirectory, string worktreePath, string newBranch, string baseRef, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		ArgumentException.ThrowIfNullOrEmpty(newBranch);
		ArgumentException.ThrowIfNullOrEmpty(baseRef);
		// git worktree add creates the leaf directory, but not missing parents — ensure the worktrees root exists.
		string? parent = Path.GetDirectoryName(worktreePath);
		if (!string.IsNullOrEmpty(parent)) {
			Directory.CreateDirectory(parent);
		}

		await RunCheckedAsync(repositoryDirectory, ["worktree", "add", "-b", newBranch, worktreePath, baseRef], ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task AttachWorktreeAsync(string repositoryDirectory, string worktreePath, string branch, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		// git worktree add creates the leaf directory, but not missing parents — ensure the worktrees root exists.
		string? parent = Path.GetDirectoryName(worktreePath);
		if (!string.IsNullOrEmpty(parent)) {
			Directory.CreateDirectory(parent);
		}

		// No -b: check out the existing branch itself, so HEAD attaches to it and commits land on that branch.
		await RunCheckedAsync(repositoryDirectory, ["worktree", "add", worktreePath, branch], ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		// -c core.longpaths=true lets git's own recursive delete handle paths past Windows' 260-char limit: a
		// worktree with a deep node_modules (pnpm's nested .pnpm store) otherwise fails removal with "Filename
		// too long". A no-op on other platforms and on shorter paths.
		string[] args = force
			? ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktreePath]
			: ["-c", "core.longpaths=true", "worktree", "remove", worktreePath];
		await RunCheckedAsync(repositoryDirectory, args, ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		var result = await RunCheckedAsync(worktreeDirectory, ["status", "--porcelain"], ct).ConfigureAwait(false);
		return result.StdOut.Trim().Length > 0;
	}

	/// <inheritdoc/>
	public async Task<WorktreeChangeState> GetChangeStateAsync(string worktreeDirectory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		var result = await RunCheckedAsync(worktreeDirectory, ["status", "--porcelain"], ct).ConfigureAwait(false);
		string[] lines = result.StdOut.Replace("\r", "", StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0) {
			return WorktreeChangeState.Clean;
		}

		// Porcelain marks an untracked path with a leading "??"; any other status code is a tracked change
		// (modified/staged/deleted/renamed). All untracked ⇒ untracked-only; otherwise tracked changes exist.
		return lines.All(line => line.StartsWith("??", StringComparison.Ordinal))
			? WorktreeChangeState.UntrackedOnly
			: WorktreeChangeState.Modified;
	}

	/// <inheritdoc/>
	public async Task<bool> IsBranchMergedAsync(string repositoryDirectory, string branch, string into, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		ArgumentException.ThrowIfNullOrEmpty(into);
		var result = await RunAsync(repositoryDirectory, ["merge-base", "--is-ancestor", branch, into], ct).ConfigureAwait(false);
		return result.ExitCode switch {
			0 => true,
			1 => false,
			_ => throw new GitException($"git merge-base --is-ancestor {branch} {into} failed (exit {result.ExitCode}): {result.StdErr.Trim()}"),
		};
	}

	/// <inheritdoc/>
	public async Task DeleteBranchAsync(string repositoryDirectory, string branch, bool force, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		await RunCheckedAsync(repositoryDirectory, ["branch", force ? "-D" : "-d", branch], ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Parses <c>git worktree list --porcelain</c> output into <see cref="GitWorktree"/> entries. Each
	/// record is a block of <c>key value</c> lines separated by blank lines; this is a pure function so it
	/// can be unit-tested without a real repository.
	/// </summary>
	public static IReadOnlyList<GitWorktree> ParsePorcelainList(string porcelain) {
		ArgumentNullException.ThrowIfNull(porcelain);
		var result = new List<GitWorktree>();
		string? path = null;
		string? head = null;
		string? branch = null;
		bool bare = false;
		bool detached = false;
		bool locked = false;
		bool prunable = false;

		void Flush() {
			if (path is not null) {
				result.Add(new GitWorktree {
					Path = path,
					Head = head,
					Branch = branch,
					IsBare = bare,
					IsDetached = detached,
					IsLocked = locked,
					IsPrunable = prunable,
				});
			}

			path = null;
			head = null;
			branch = null;
			bare = false;
			detached = false;
			locked = false;
			prunable = false;
		}

		foreach (string raw in porcelain.Split('\n')) {
			string line = raw.TrimEnd('\r');
			if (line.Length == 0) {
				Flush();
				continue;
			}

			int space = line.IndexOf(' ');
			string key = space < 0 ? line : line[..space];
			string value = space < 0 ? string.Empty : line[(space + 1)..];
			switch (key) {
				case "worktree":
					Flush();
					path = value;
					break;
				case "HEAD":
					head = value;
					break;
				case "branch":
					branch = value.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? value[HeadsPrefix.Length..] : value;
					break;
				case "bare":
					bare = true;
					break;
				case "detached":
					detached = true;
					break;
				case "locked":
					locked = true;
					break;
				case "prunable":
					prunable = true;
					break;
				default:
					break;
			}
		}

		Flush();
		return result;
	}

	private static async Task<GitResult> RunCheckedAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) {
		var result = await RunAsync(workingDirectory, args, ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			throw new GitException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
		}

		return result;
	}

	private static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) {
		var info = new ProcessStartInfo {
			FileName = "git",
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = new Process { StartInfo = info };
		try {
			process.Start();
		} catch (Win32Exception ex) {
			throw new GitException("Unable to start 'git' — is it installed and on PATH?", ex);
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
		var stderrTask = process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct).ConfigureAwait(false);
		string stdout = await stdoutTask.ConfigureAwait(false);
		string stderr = await stderrTask.ConfigureAwait(false);
		return new GitResult(process.ExitCode, stdout, stderr);
	}

	private readonly record struct GitResult(int ExitCode, string StdOut, string StdErr);
}
