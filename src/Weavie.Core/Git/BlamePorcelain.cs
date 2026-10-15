using System.Globalization;

namespace Weavie.Core.Git;

/// <summary>
/// Parses <c>git blame --porcelain</c>. Git emits a commit's headers only the first time it appears and repeats
/// just the <c>sha origLine finalLine</c> line after that, which is already the deduplicated shape
/// <see cref="GitBlame"/> wants — so the parse carries each commit forward by sha rather than re-reading it.
/// Pure, so it is tested without a repository.
/// </summary>
public static class BlamePorcelain {
	/// <summary>The sha Git reports for a line that exists only in the working tree.</summary>
	public const string UncommittedSha = "0000000000000000000000000000000000000000";

	/// <summary>Parses porcelain <paramref name="output"/> into a blame; empty output yields <see cref="GitBlame.Empty"/>.</summary>
	public static GitBlame Parse(string output) {
		ArgumentNullException.ThrowIfNull(output);
		var commits = new List<BlameCommit>();
		var indexBySha = new Dictionary<string, int>(StringComparer.Ordinal);
		var lineCommits = new List<int>();
		var lineOriginals = new List<int>();
		string? sha = null;
		int finalLine = 0;
		int originalLine = 0;
		var pending = new PendingCommit();

		foreach (string raw in output.Split('\n')) {
			string line = raw.EndsWith('\r') ? raw[..^1] : raw;
			if (line.Length == 0) {
				continue;
			}

			// The content line (tab-prefixed) closes the entry: the headers above it are complete.
			if (line[0] == '\t') {
				if (sha is null) {
					continue;
				}

				if (!indexBySha.TryGetValue(sha, out int index)) {
					index = commits.Count;
					indexBySha[sha] = index;
					commits.Add(pending.ToCommit(sha));
				}

				Assign(lineCommits, lineOriginals, finalLine, index, originalLine);
				sha = null;
				continue;
			}

			// Only a successful header may move the entry's position: the key lines that follow it are offered
			// here first, and must leave the group they belong to intact.
			if (TryParseEntryHeader(line, out string entrySha, out int entryOriginal, out int entryFinal)) {
				sha = entrySha;
				originalLine = entryOriginal;
				finalLine = entryFinal;
				pending = new PendingCommit();
				continue;
			}

			int space = line.IndexOf(' ', StringComparison.Ordinal);
			string key = space < 0 ? line : line[..space];
			string value = space < 0 ? string.Empty : line[(space + 1)..];
			switch (key) {
				case "author":
					pending.Author = value;
					break;
				case "author-mail":
					pending.Email = value.Trim('<', '>');
					break;
				case "author-time":
					pending.TimeUnix = long.TryParse(value, CultureInfo.InvariantCulture, out long time) ? time : 0;
					break;
				case "summary":
					pending.Summary = value;
					break;
				default:
					break;
			}
		}

		return lineCommits.Count == 0
			? GitBlame.Empty
			: new GitBlame(commits, lineCommits, lineOriginals);
	}

	// "<40-hex sha> <line in the commit> <line in the file> [<lines in this group>]".
	private static bool TryParseEntryHeader(string line, out string sha, out int original, out int final) {
		sha = string.Empty;
		original = 0;
		final = 0;
		string[] parts = line.Split(' ');
		if (parts.Length is < 3 or > 4 || parts[0].Length != 40) {
			return false;
		}

		foreach (char c in parts[0]) {
			if (!char.IsAsciiHexDigitLower(c)) {
				return false;
			}
		}

		if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out original)
			|| !int.TryParse(parts[2], CultureInfo.InvariantCulture, out final)
			|| original <= 0
			|| final <= 0) {
			return false;
		}

		sha = parts[0];
		return true;
	}

	// Git walks the file in group order, not line order, so grow to `finalLine` and write at its index.
	private static void Assign(List<int> lineCommits, List<int> lineOriginals, int finalLine, int commitIndex, int originalLine) {
		while (lineCommits.Count < finalLine) {
			lineCommits.Add(-1);
			lineOriginals.Add(0);
		}

		lineCommits[finalLine - 1] = commitIndex;
		lineOriginals[finalLine - 1] = originalLine;
	}

	private sealed class PendingCommit {
		public string Author { get; set; } = string.Empty;

		public string Email { get; set; } = string.Empty;

		public long TimeUnix { get; set; }

		public string Summary { get; set; } = string.Empty;

		public BlameCommit ToCommit(string sha) =>
			new(sha, Author, Email, TimeUnix, Summary, string.Equals(sha, UncommittedSha, StringComparison.Ordinal));
	}
}
