using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Weavie.Core.Git;

// The blame surface: who last touched each line, the commits behind a line or a file, and the hunk one of those
// commits changed. Read-only probes, so they all pass --no-optional-locks and never take .git/index.lock.
public sealed partial class GitService {
	// Enough context to read the change in the popover without scrolling it into a second screen.
	private const int HunkContextLines = 3;

	// Unit separator: never appears in a name, subject, or sha, so the record splits unambiguously.
	private const string LogFormat = "--format=%H%x1f%an%x1f%at%x1f%s";

	/// <inheritdoc/>
	public async Task<GitBlame> BlameFileAsync(string worktreeDirectory, string path, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		ArgumentException.ThrowIfNullOrEmpty(path);
		var result = await RunCheckedAsync(
			worktreeDirectory,
			["--no-optional-locks", "blame", "--porcelain", "--", path],
			ct).ConfigureAwait(false);
		return BlamePorcelain.Parse(result.StdOut);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<GitCommit>> LogFileAsync(
		string worktreeDirectory,
		string path,
		int limit,
		CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
		var result = await RunCheckedAsync(
			worktreeDirectory,
			[
				"--no-optional-locks",
				"log",
				"--follow",
				LogFormat,
				"--max-count=" + limit.ToString(CultureInfo.InvariantCulture),
				"--",
				path,
			],
			ct).ConfigureAwait(false);
		return ParseLog(result.StdOut);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<GitLineCommit>> LogLinesAsync(
		string worktreeDirectory,
		string startCommit,
		string path,
		int startLine,
		int endLine,
		int limit,
		CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startLine);
		ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
		if (!IsCommitSha(startCommit)) {
			throw new GitException($"'{startCommit}' is not a commit sha.");
		}

		// -L takes its path inside the argument, so a path that starts with '-' still cannot read as an option.
		string range = string.Create(CultureInfo.InvariantCulture, $"{startLine},{endLine}:{path}");
		// The traversal starts at startCommit, not HEAD, because the line numbers come from a blame of the
		// working tree and only mean anything in the commit that blame attributed them to. Walking from HEAD
		// with those numbers reports a different line whenever the file has uncommitted line-count changes
		// above it — and fails outright when the working tree is the longer of the two.
		//
		// The patch stays on despite being far too narrow to read: its @@ header is where Git reports the
		// line's number in each older commit, the anchor CommitHunkAsync needs to show that change in context.
		var result = await RunCheckedAsync(
			worktreeDirectory,
			[
				"--no-optional-locks",
				"log",
				"-L",
				range,
				startCommit,
				LogFormat,
				"--max-count=" + limit.ToString(CultureInfo.InvariantCulture),
			],
			ct).ConfigureAwait(false);
		return ParseLineLog(result.StdOut);
	}

	/// <inheritdoc/>
	public async Task<GitDiffHunk?> CommitHunkAsync(
		string worktreeDirectory,
		string commit,
		string path,
		int line,
		CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(worktreeDirectory);
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(line);
		if (!IsCommitSha(commit)) {
			throw new GitException($"'{commit}' is not a commit sha.");
		}

		// -m --first-parent gives a merge the same shape as an ordinary commit — its diff against the branch it
		// merged into — instead of the empty default, so a line blamed to a merge still resolves to a hunk.
		var result = await RunCheckedAsync(
			worktreeDirectory,
			[
				"--no-optional-locks",
				"show",
				"--format=",
				"--unified=" + HunkContextLines.ToString(CultureInfo.InvariantCulture),
				"-m",
				"--first-parent",
				commit,
				"--",
				path,
			],
			ct).ConfigureAwait(false);
		return UnifiedDiff.HunkContaining(result.StdOut, line);
	}

	/// <summary>True when <paramref name="value"/> is a full lowercase-hex commit sha — the only form these probes accept.</summary>
	public static bool IsCommitSha(string value) {
		if (value is not { Length: 40 }) {
			return false;
		}

		foreach (char c in value) {
			if (!char.IsAsciiHexDigitLower(c)) {
				return false;
			}
		}

		return true;
	}

	private static IReadOnlyList<GitCommit> ParseLog(string output) {
		var commits = new List<GitCommit>();
		foreach (string raw in output.Split('\n')) {
			if (TryParseLogRecord(raw, out var commit)) {
				commits.Add(commit);
			}
		}

		return commits;
	}

	// Each record is its --format line followed by that commit's (deliberately unread) patch; the first @@ header
	// under a record carries the tracked line's number there.
	private static IReadOnlyList<GitLineCommit> ParseLineLog(string output) {
		var commits = new List<GitLineCommit>();
		foreach (string raw in output.Split('\n')) {
			string line = raw.Trim('\r');
			if (TryParseLogRecord(line, out var commit)) {
				commits.Add(new GitLineCommit(commit, 0));
			} else if (commits.Count > 0
				&& commits[^1].Line == 0
				&& UnifiedDiff.TryParseNewStart(line, out int newStart)) {
				commits[^1] = commits[^1] with { Line = newStart };
			}
		}

		return commits;
	}

	private static bool TryParseLogRecord(string line, [NotNullWhen(true)] out GitCommit? commit) {
		string[] fields = line.Trim('\r').Split('\x1f');
		commit = fields.Length == 4
			&& IsCommitSha(fields[0])
			&& long.TryParse(fields[2], CultureInfo.InvariantCulture, out long time)
				? new GitCommit(fields[0], fields[1], time, fields[3])
				: null;
		return commit is not null;
	}
}
