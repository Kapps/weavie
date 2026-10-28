using System.Globalization;

namespace Weavie.Core.Git;

// Which branch the checked-out branch was created from, read from git's own record of it, so a branch stacked on an
// unmerged one compares with that parent instead of the default branch.
public sealed partial class GitService {
	private const string CreatedFrom = "branch: Created from ";
	private const string MovingFrom = "checkout: moving from ";
	private const string ReflogFormat = "--format=%gd%x1f%gs";

	/// <summary>
	/// The branch HEAD's branch was created from, as its reflog records it (or, created from <c>HEAD</c>, the branch
	/// HEAD was then on). Answers <paramref name="preferred"/> (the default branch) unless that parent is another
	/// branch that still exists and forked from HEAD's history somewhere <paramref name="preferred"/> doesn't reach —
	/// so only a branch stacked on an unmerged one resolves elsewhere.
	/// </summary>
	public async Task<string> ResolveParentBranchAsync(string directory, string preferred, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(directory);
		ArgumentException.ThrowIfNullOrEmpty(preferred);
		if (await GetCurrentBranchAsync(directory, ct).ConfigureAwait(false) is not { } current
			|| await ReadStartPointAsync(directory, current, ct).ConfigureAwait(false) is not { } parent
			|| parent.StartsWith('-')) {
			return preferred;
		}

		var full = await RunAsync(directory, ["rev-parse", "--verify", "--quiet", "--symbolic-full-name", parent], ct).ConfigureAwait(false);
		if (!TryParseRef(full.StdOut.Trim(), out _, out string parentBranch) || parentBranch == current) {
			return preferred;
		}

		// Both branches may have merged the default branch in, so any of their merge-bases off it marks the fork.
		var bases = await RunAsync(directory, ["merge-base", "--all", parent, "HEAD"], ct).ConfigureAwait(false);
		foreach (string fork in Lines(bases.StdOut)) {
			if (!await IsAncestorAsync(directory, fork, preferred, ct).ConfigureAwait(false)) {
				return parent;
			}
		}

		return preferred;
	}

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
