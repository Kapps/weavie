using System.Globalization;

namespace Weavie.Core.Git;

// The ref a whole-branch review compares with. Git has no notion of a parent branch, so this layers what is known:
// the user's own choice, the open PR's base, the parent Weavie recorded, git's reflog record, then the default branch.
public sealed partial class GitService {
	private const string CreatedFrom = "branch: Created from ";
	private const string MovingFrom = "checkout: moving from ";
	private const string ReflogFormat = "--format=%gd%x1f%gs";
	private const string ChosenBaseKey = "weavie-review-base";
	private const string ParentKey = "weavie-parent";

	/// <summary>
	/// The ref HEAD's branch is reviewed against: the base the user chose for it, else
	/// <paramref name="pullRequestBase"/> (its open PR's base), else the branch it was created from — as Weavie
	/// recorded it, or git's reflog did — when that is another branch that forked from HEAD's history somewhere
	/// <paramref name="preferred"/> (the default branch) doesn't reach, else <paramref name="preferred"/>.
	/// </summary>
	public async Task<string> ResolveBranchBaseAsync(
		string directory, string preferred, string? pullRequestBase, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		ArgumentException.ThrowIfNullOrEmpty(preferred);
		string? current = await GetCurrentBranchAsync(directory, ct).ConfigureAwait(false);
		return current is not null
			&& await ReadBranchConfigAsync(directory, current, ChosenBaseKey, ct).ConfigureAwait(false) is { } chosen
			&& await ResolveCommitAsync(directory, chosen, ct).ConfigureAwait(false) is not null
			? chosen
			: await ResolveDetectedBaseAsync(directory, current, preferred, pullRequestBase, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Remembers <paramref name="reference"/> as the base HEAD's branch is reviewed against when it names another
	/// branch; choosing the base Weavie would detect anyway forgets the choice, so the detected base keeps tracking.
	/// Answers whether the base HEAD's branch is reviewed against changed.
	/// </summary>
	public async Task<bool> ChooseBranchBaseAsync(
		string directory, string reference, string preferred, string? pullRequestBase, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		ArgumentException.ThrowIfNullOrEmpty(reference);
		ArgumentException.ThrowIfNullOrEmpty(preferred);
		if (await GetCurrentBranchAsync(directory, ct).ConfigureAwait(false) is not { } current
			|| !await IsOtherBranchAsync(directory, current, reference, ct).ConfigureAwait(false)) {
			return false;
		}

		string before = await ResolveBranchBaseAsync(directory, preferred, pullRequestBase, ct).ConfigureAwait(false);
		string key = BranchConfigKey(current, ChosenBaseKey);
		if (reference == await ResolveDetectedBaseAsync(directory, current, preferred, pullRequestBase, ct).ConfigureAwait(false)) {
			var unset = await RunAsync(directory, ["config", "--local", "--unset", key], ct).ConfigureAwait(false);
			// Exit 5: nothing was chosen.
			if (unset.ExitCode is not (0 or 5)) {
				throw new GitException($"git config --unset {key} failed (exit {unset.ExitCode}): {unset.StdErr.Trim()}");
			}
		} else {
			await RunCheckedAsync(directory, ["config", "--local", key, reference], ct).ConfigureAwait(false);
		}

		return reference != before;
	}

	/// <summary>Whether a rebase is under way, during which HEAD is detached from the branch being rebased.</summary>
	public async Task<bool> IsRebasingAsync(string directory, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		var paths = await RunCheckedAsync(directory, ["rev-parse", "--git-path", "rebase-merge", "--git-path", "rebase-apply"], ct)
			.ConfigureAwait(false);
		return Lines(paths.StdOut).Any(path => Directory.Exists(Path.GetFullPath(path, directory)));
	}

	/// <summary>Records <paramref name="parent"/> as the branch <paramref name="branch"/> was created from.</summary>
	public async Task RecordParentBranchAsync(string directory, string branch, string parent, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		ArgumentException.ThrowIfNullOrEmpty(branch);
		ArgumentException.ThrowIfNullOrEmpty(parent);
		await RunCheckedAsync(directory, ["config", "--local", BranchConfigKey(branch, ParentKey), parent], ct).ConfigureAwait(false);
	}

	private async Task<string> ResolveDetectedBaseAsync(
		string directory, string? current, string preferred, string? pullRequestBase, CancellationToken ct) {
		if (pullRequestBase is not null && await ResolveCommitAsync(directory, pullRequestBase, ct).ConfigureAwait(false) is not null) {
			return pullRequestBase;
		}

		if (current is null
			|| (await ReadBranchConfigAsync(directory, current, ParentKey, ct).ConfigureAwait(false)
				?? await ReadStartPointAsync(directory, current, ct).ConfigureAwait(false)) is not { } parent
			|| !await IsOtherBranchAsync(directory, current, parent, ct).ConfigureAwait(false)) {
			return preferred;
		}

		// Both branches may have merged the default branch in, so any of their merge-bases off it marks the fork.
		var bases = await RunAsync(directory, ["merge-base", "--all", parent, "HEAD"], ct).ConfigureAwait(false);
		// Exit 1: no common history.
		if (bases.ExitCode is not (0 or 1)) {
			throw new GitException($"git merge-base --all {parent} HEAD failed (exit {bases.ExitCode}): {bases.StdErr.Trim()}");
		}

		foreach (string fork in Lines(bases.StdOut)) {
			if (!await IsAncestorAsync(directory, fork, preferred, ct).ConfigureAwait(false)) {
				return parent;
			}
		}

		return preferred;
	}

	// Whether reference names a local or remote-tracking branch other than current or a remote copy of it.
	private static async Task<bool> IsOtherBranchAsync(string directory, string current, string reference, CancellationToken ct) {
		if (reference.StartsWith('-')) {
			return false;
		}

		var full = await RunAsync(directory, ["rev-parse", "--verify", "--quiet", "--symbolic-full-name", reference], ct).ConfigureAwait(false);
		return TryParseRef(full.StdOut.Trim(), out _, out string branch) && branch != current;
	}

	private static async Task<string?> ReadBranchConfigAsync(string directory, string branch, string key, CancellationToken ct) {
		var result = await RunAsync(directory, ["config", "--get", BranchConfigKey(branch, key)], ct).ConfigureAwait(false);
		// Only exit 1 means the key is unset.
		return result.ExitCode switch {
			0 => result.StdOut.Trim() is { Length: > 0 } value ? value : null,
			1 => null,
			_ => throw new GitException($"git config --get {BranchConfigKey(branch, key)} failed (exit {result.ExitCode}): {result.StdErr.Trim()}"),
		};
	}

	// git config keeps a branch's settings in its own section, which `git branch -d`/`-m` delete and move with it.
	private static string BranchConfigKey(string branch, string key) => $"branch.{branch}.{key}";

	// The start point git recorded when the branch was created: its oldest reflog entry, or for a branch created from
	// HEAD, the branch HEAD moved away from when it first arrived on this branch after that.
	private static async Task<string?> ReadStartPointAsync(string directory, string branch, CancellationToken ct) {
		var created = await RunCheckedAsync(
			directory, ["reflog", "show", "--date=unix", ReflogFormat, HeadsPrefix + branch, "--"], ct).ConfigureAwait(false);
		if (Lines(created.StdOut).Select(ParseReflogEntry).LastOrDefault() is not { } creation
			|| !creation.Subject.StartsWith(CreatedFrom, StringComparison.Ordinal)) {
			return null;
		}

		string start = creation.Subject[CreatedFrom.Length..];
		if (start != "HEAD") {
			return start;
		}

		string arrival = " to " + branch;
		var moves = await RunCheckedAsync(
			directory,
			["log", "--walk-reflogs", "--date=unix", ReflogFormat, "--fixed-strings", $"--grep-reflog={arrival}", "HEAD", "--"],
			ct).ConfigureAwait(false);
		var first = Lines(moves.StdOut).Select(ParseReflogEntry).LastOrDefault(entry => entry is { } move
			&& move.Time >= creation.Time
			&& move.Subject.StartsWith(MovingFrom, StringComparison.Ordinal)
			&& move.Subject.EndsWith(arrival, StringComparison.Ordinal));
		return first?.Subject[MovingFrom.Length..^arrival.Length];
	}

	// "<ref>@{<unix time>}<US><subject>", as ReflogFormat prints an entry with --date=unix.
	private static ReflogEntry? ParseReflogEntry(string line) {
		int open = line.IndexOf("@{", StringComparison.Ordinal);
		int close = open < 0 ? -1 : line.IndexOf("}\x1f", open, StringComparison.Ordinal);
		return close >= 0 && long.TryParse(line[(open + 2)..close], CultureInfo.InvariantCulture, out long time)
			? new ReflogEntry(time, line[(close + 2)..])
			: null;
	}

	private static async Task<bool> IsAncestorAsync(string directory, string ancestor, string descendant, CancellationToken ct) {
		var result = await RunAsync(directory, ["merge-base", "--is-ancestor", ancestor, descendant], ct).ConfigureAwait(false);
		return result.ExitCode switch {
			0 => true,
			1 => false,
			_ => throw new GitException($"git merge-base --is-ancestor failed (exit {result.ExitCode}): {result.StdErr.Trim()}"),
		};
	}

	private static string[] Lines(string output) =>
		output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private sealed record ReflogEntry(long Time, string Subject);
}
