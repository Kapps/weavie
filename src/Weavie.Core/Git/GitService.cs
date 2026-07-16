using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Git;

/// <summary>
/// <see cref="IGitService"/> backed by the <c>git</c> executable on <c>PATH</c>. Each call spawns a
/// short-lived <c>git</c> process (a transient one-shot helper, exempt from <c>ProcessSupervisor</c>),
/// captures stdout/stderr, and throws <see cref="GitException"/> when a required command fails.
/// </summary>
public sealed class GitService : IGitService {
	private const string HeadsPrefix = "refs/heads/";

	// A read-only dirty probe: `--no-optional-locks` refreshes the index in-core instead of taking
	// `.git/index.lock`, so this background/footer poll can never collide with a concurrent `git diff` or
	// `git add` on the same repo (the index-lock race behind the diff-against CI flakes).
	private static readonly string[] PorcelainStatusArgs = ["--no-optional-locks", "status", "--porcelain"];
	private static readonly string[] PorcelainStatusZArgs = ["--no-optional-locks", "status", "--porcelain", "-z"];

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
	public async Task<IReadOnlyList<string>> ListRefsAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		// One for-each-ref over both scopes: heads sort before remotes ("h" < "r"), so the typeahead is local-first.
		var result = await RunCheckedAsync(directory, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], ct).ConfigureAwait(false);
		const string remotesPrefix = "refs/remotes/";
		var refs = new List<string>();
		foreach (string line in result.StdOut.Replace("\r", "", StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			if (line.StartsWith(HeadsPrefix, StringComparison.Ordinal)) {
				refs.Add(line[HeadsPrefix.Length..]);
			} else if (line.StartsWith(remotesPrefix, StringComparison.Ordinal)) {
				string name = line[remotesPrefix.Length..];
				// Skip a remote's symbolic HEAD — exactly "<remote>/HEAD", no deeper slash — an alias, not a branch.
				// A real branch that merely ends in HEAD (e.g. origin/feature/HEAD) has a deeper slash, so it stays.
				bool isRemoteHead = name.EndsWith("/HEAD", StringComparison.Ordinal)
					&& !name[..^"/HEAD".Length].Contains('/', StringComparison.Ordinal);
				if (!isRemoteHead) {
					refs.Add(name);
				}
			}
		}

		return refs;
	}

	/// <summary>
	/// Whether <paramref name="name"/> is a syntactically valid git branch name — a subset of
	/// <c>git check-ref-format</c>'s rules. Applied at the trust boundary before a (web-supplied) name reaches
	/// <c>git</c>, so it can't be parsed as an option (a leading <c>-</c>) or a malformed ref. The
	/// <c>ArgumentList</c> launch already rules out shell injection; this closes option/ref smuggling.
	/// </summary>
	public static bool IsValidBranchName(string name) {
		if (string.IsNullOrEmpty(name) || name.Length > 255) {
			return false;
		}

		if (name[0] is '-' or '.' or '/' || name[^1] is '/' or '.') {
			return false;
		}

		if (name == "@"
			|| name.EndsWith(".lock", StringComparison.Ordinal)
			|| name.Contains("..", StringComparison.Ordinal)
			|| name.Contains("//", StringComparison.Ordinal)
			|| name.Contains("@{", StringComparison.Ordinal)) {
			return false;
		}

		foreach (char c in name) {
			// Control chars + space, DEL, and git's ref-illegal set ~ ^ : ? * [ \.
			if (c <= ' ' || c == '\x7f' || "~^:?*[\\".Contains(c, StringComparison.Ordinal)) {
				return false;
			}
		}

		return true;
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
		EnsureParentDirectory(worktreePath);
		await RunCheckedAsync(repositoryDirectory, ["worktree", "add", "-b", newBranch, worktreePath, baseRef], ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task AttachWorktreeAsync(string repositoryDirectory, string worktreePath, string branch, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		EnsureParentDirectory(worktreePath);
		// No -b: check out the existing branch, so HEAD attaches to it and commits land there.
		await RunCheckedAsync(repositoryDirectory, ["worktree", "add", worktreePath, branch], ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task RemoveWorktreeAsync(string repositoryDirectory, string worktreePath, bool force, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(worktreePath);
		// core.longpaths=true lets git's recursive delete handle paths past Windows' 260-char limit (e.g. a
		// deep pnpm node_modules) that otherwise fail with "Filename too long"; a no-op elsewhere.
		string[] args = force
			? ["-c", "core.longpaths=true", "worktree", "remove", "--force", worktreePath]
			: ["-c", "core.longpaths=true", "worktree", "remove", worktreePath];
		await RunCheckedAsync(repositoryDirectory, args, ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<bool> HasUncommittedChangesAsync(string worktreeDirectory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		var result = await RunCheckedAsync(worktreeDirectory, PorcelainStatusArgs, ct).ConfigureAwait(false);
		return result.StdOut.Trim().Length > 0;
	}

	/// <inheritdoc/>
	public async Task<WorktreeChangeStatus> GetChangeStateAsync(string worktreeDirectory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		// -z NUL-separates entries and drops C-style path quoting. A rename/copy is followed by its bare source
		// path, which is consumed with the status record rather than surfaced as a second changed file.
		var result = await RunCheckedAsync(worktreeDirectory, PorcelainStatusZArgs, ct).ConfigureAwait(false);
		string[] entries = result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		var tracked = new List<string>();
		var untracked = new List<string>();
		for (int i = 0; i < entries.Length; i++) {
			string entry = entries[i];
			if (entry.StartsWith("?? ", StringComparison.Ordinal)) {
				untracked.Add(entry[3..]);
			} else {
				tracked.Add(entry[3..]);
				if (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C') {
					i++;
				}
			}
		}

		var state = tracked.Count > 0 ? WorktreeChangeState.Modified
			: untracked.Count > 0 ? WorktreeChangeState.UntrackedOnly
			: WorktreeChangeState.Clean;
		return new WorktreeChangeStatus(state, tracked, untracked);
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

	/// <inheritdoc/>
	public async Task FetchAsync(string repositoryDirectory, string remote, string refName, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(remote);
		ArgumentException.ThrowIfNullOrEmpty(refName);
		// remote ("origin") is host-derived; refName is a branch name validated upstream — pass both as explicit
		// args so neither can be read as an option, and never accept a raw web-supplied refspec.
		await RunCheckedAsync(repositoryDirectory, ["fetch", remote, refName], ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<string?> GetRemoteUrlAsync(string repositoryDirectory, string remote, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(remote);
		// `config --get remote.<x>.url` is the *configured* URL (what identifies the repo); `git remote get-url`
		// would instead apply any insteadOf rewrite, returning the transport URL rather than the github.com one.
		var result = await RunAsync(repositoryDirectory, ["config", "--get", $"remote.{remote}.url"], ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			return null;
		}

		string url = result.StdOut.Trim();
		return url.Length == 0 ? null : url;
	}

	/// <inheritdoc/>
	public async Task<string?> MergeBaseAsync(string repositoryDirectory, string a, string b, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(a);
		ArgumentException.ThrowIfNullOrEmpty(b);
		var result = await RunAsync(repositoryDirectory, ["merge-base", a, b], ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			return null;
		}

		string sha = result.StdOut.Trim();
		return sha.Length == 0 ? null : sha;
	}

	/// <inheritdoc/>
	public async Task<string?> ResolveCommitAsync(string repositoryDirectory, string reference, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(reference);
		// The trust-boundary check for a web-supplied ref: never let it be read as an option. (--end-of-options
		// would also cover it, but rejecting here keeps the guarantee independent of the git version.)
		if (reference.StartsWith('-')) {
			return null;
		}

		// ^{commit} peels tags to commits, so any commit-ish resolves to what a diff can use.
		var result = await RunAsync(repositoryDirectory, ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"], ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			return null;
		}

		string sha = result.StdOut.Trim();
		return sha.Length == 0 ? null : sha;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<DiffFileChange>> DiffWorktreeAsync(string repositoryDirectory, string baseRef, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(baseRef);
		// No second ref ⇒ diff against the working tree, so uncommitted edits are included (unlike DiffRefsAsync).
		var result = await RunCheckedAsync(repositoryDirectory, ["diff", "--numstat", "--no-renames", baseRef, "--"], ct).ConfigureAwait(false);
		var changes = new List<DiffFileChange>(ParseNumstat(result.StdOut));
		// `git diff` skips untracked files, but to a user a brand-new file IS an uncommitted change — surface
		// each (gitignore honored) as all-added rather than silently absent from the review.
		var untracked = await RunCheckedAsync(repositoryDirectory, ["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
		foreach (string path in untracked.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
			changes.Add(new DiffFileChange { Path = path, Added = CountLines(Path.Combine(repositoryDirectory, path)), Removed = 0 });
		}

		return [.. changes.OrderBy(c => c.Path, StringComparer.Ordinal)];
	}

	// The added-line count for an untracked file (display-only, mirroring numstat); 0 when it vanished mid-read.
	private static int CountLines(string absolutePath) {
		try {
			return File.ReadLines(absolutePath).Count();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return 0;
		}
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<DiffFileChange>> DiffRefsAsync(string repositoryDirectory, string fromRef, string toRef, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(fromRef);
		ArgumentException.ThrowIfNullOrEmpty(toRef);
		// --no-renames keeps each line a simple "added\tremoved\tpath"; "--" ends options so refs can't be misread.
		var result = await RunCheckedAsync(repositoryDirectory, ["diff", "--numstat", "--no-renames", fromRef, toRef, "--"], ct).ConfigureAwait(false);
		return ParseNumstat(result.StdOut);
	}

	/// <inheritdoc/>
	public async Task<string> ShowFileAtRefAsync(string repositoryDirectory, string reference, string path, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(repositoryDirectory);
		ArgumentException.ThrowIfNullOrEmpty(reference);
		ArgumentException.ThrowIfNullOrEmpty(path);
		// A non-zero exit means the file is absent at that ref (added in the PR) — an empty baseline, not an error.
		var result = await RunAsync(repositoryDirectory, ["show", $"{reference}:{path}"], ct).ConfigureAwait(false);
		return result.ExitCode == 0 ? result.StdOut : string.Empty;
	}

	/// <summary>
	/// The workspace files git would surface — tracked plus untracked — with <c>.gitignore</c> (and the other
	/// standard excludes: <c>.git/info/exclude</c>, the global excludesfile) honored, as repo-relative paths.
	/// Returns <c>null</c> when <paramref name="directory"/> isn't a git repo or git is unavailable, so a caller
	/// can fall back to a plain directory walk.
	/// </summary>
	public async Task<IReadOnlyList<string>?> ListWorkspaceFilesAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		GitResult result;
		try {
			// -z keeps names with newlines safe; --exclude-standard applies the ignore rules; --cached is the
			// tracked set and --others adds untracked-but-not-ignored files (so a brand-new file still opens).
			result = await RunAsync(directory, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
		} catch (GitException) {
			return null; // git missing — caller falls back to the plain walk
		}

		return result.ExitCode == 0 ? result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries) : null;
	}

	/// <summary>
	/// Searches the worktree's file contents for <paramref name="query"/> under <paramref name="options"/> across
	/// tracked + untracked-but-not-ignored files, skipping binaries. An empty query returns no matches without
	/// running git. Results are capped (<see cref="GitGrep.MatchCap"/>) with <see cref="GrepResult.Truncated"/>
	/// set. A bad query (e.g. an invalid regex) throws <see cref="GitException"/> carrying git's message.
	/// </summary>
	public async Task<GrepResult> GrepAsync(string worktreeDirectory, string query, GrepOptions options, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(options);
		if (query.Length == 0) {
			return new GrepResult { Matches = [], Truncated = false };
		}

		var result = await RunAsync(worktreeDirectory, GitGrep.BuildArgs(query, options), ct).ConfigureAwait(false);
		if (result.ExitCode is not (0 or 1)) { // exit 1 = no matches
			throw new GitException($"git grep failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
		}

		return GitGrep.Parse(result.StdOut, GitGrep.MatchCap);
	}

	/// <summary>Parses <c>git diff --numstat</c> output ("added\tremoved\tpath" per line; binary files report "-"). Pure, for tests.</summary>
	public static IReadOnlyList<DiffFileChange> ParseNumstat(string numstat) {
		ArgumentNullException.ThrowIfNull(numstat);
		var result = new List<DiffFileChange>();
		foreach (string raw in numstat.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
			string[] parts = raw.Split('\t');
			if (parts.Length < 3) {
				continue;
			}

			result.Add(new DiffFileChange {
				Path = parts[2].Trim(),
				Added = int.TryParse(parts[0], out int added) ? added : 0,
				Removed = int.TryParse(parts[1], out int removed) ? removed : 0,
			});
		}

		return result;
	}

	/// <summary>
	/// Parses <c>git worktree list --porcelain</c> output into <see cref="GitWorktree"/> entries — each a
	/// block of <c>key value</c> lines separated by blank lines. Pure, so it's testable without a repository.
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

	// git worktree add creates the leaf directory but not missing parents — ensure the root exists.
	private static void EnsureParentDirectory(string worktreePath) {
		string? parent = Path.GetDirectoryName(worktreePath);
		if (!string.IsNullOrEmpty(parent)) {
			Directory.CreateDirectory(parent);
		}
	}

	private static async Task<GitResult> RunCheckedAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) {
		var result = await RunAsync(workingDirectory, args, ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			throw new GitException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.StdErr.Trim()}");
		}

		return result;
	}

	private static async Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> args, CancellationToken ct) {
		if (!Directory.Exists(workingDirectory)) {
			throw new GitException($"Git working directory does not exist: {workingDirectory}");
		}

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
			throw new GitException($"Unable to start 'git' from '{workingDirectory}': {ex.Message}", ex);
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
