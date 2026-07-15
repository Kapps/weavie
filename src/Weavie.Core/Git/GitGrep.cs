using System.Text;

namespace Weavie.Core.Git;

/// <summary>
/// The pure half of find-in-files: maps a query + <see cref="GrepOptions"/> onto <c>git grep</c> arguments and
/// parses its NUL-delimited output. Separate from <see cref="GitService"/> so the flag/pathspec mapping and the
/// parser are testable without git.
/// </summary>
public static class GitGrep {
	/// <summary>The most matches a content search returns; hitting it sets <see cref="GrepResult.Truncated"/> so the user is told the list is incomplete.</summary>
	public const int MatchCap = 500;

	/// <summary>
	/// The <c>git grep</c> argv for <paramref name="query"/> under <paramref name="options"/>: NUL-delimited
	/// path/line/column output (<c>-n --column -z</c>), binaries skipped, tracked + untracked files searched.
	/// "-e &lt;query&gt; --" keeps the query an operand (never read as an option even when it starts with "-"),
	/// and the include/exclude globs become trailing pathspecs.
	/// </summary>
	public static IReadOnlyList<string> BuildArgs(string query, GrepOptions options) {
		ArgumentNullException.ThrowIfNull(query);
		ArgumentNullException.ThrowIfNull(options);
		var args = new List<string> { "grep", "-n", "--column", "-z", "-I", "--no-color", "--untracked" };
		if (!options.ExcludeGitignored) {
			// --untracked already searches untracked-but-not-ignored files; --no-exclude-standard drops the
			// ignore rules on top, so gitignored files (node_modules, build output) are searched too.
			args.Add("--no-exclude-standard");
		}

		args.Add(options.Regex ? "-E" : "-F");
		if (!options.CaseSensitive) {
			args.Add("-i");
		}

		if (options.WholeWord) {
			args.Add("-w");
		}

		args.AddRange(["-e", query, "--"]);
		args.AddRange(ExpandPathspecs(options.Include, "glob"));
		args.AddRange(ExpandPathspecs(options.Exclude, "exclude,glob"));
		return args;
	}

	/// <summary>
	/// A comma-separated glob list as git pathspecs (VS Code's include/exclude semantics): a bare name matches
	/// at any depth as a file or a directory, a path containing '/' anchors at the repo root, and a trailing '/'
	/// means the whole directory. <paramref name="magic"/> is the pathspec magic ("glob", or "exclude,glob").
	/// </summary>
	public static IEnumerable<string> ExpandPathspecs(string globs, string magic) {
		ArgumentNullException.ThrowIfNull(globs);
		ArgumentException.ThrowIfNullOrEmpty(magic);
		foreach (string raw in globs.Split(',')) {
			string token = raw.Trim().TrimStart('/');
			if (token.StartsWith("./", StringComparison.Ordinal)) {
				token = token[2..];
			}

			if (token.Length == 0) {
				continue;
			}

			if (token.EndsWith('/')) {
				token += "**";
			}

			if (token.Contains('/')) {
				yield return $":({magic}){token}";
			} else {
				// A bare name may be a file glob (*.ts) or a directory (node_modules): match both, at any depth.
				yield return $":({magic})**/{token}";
				yield return $":({magic})**/{token}/**";
			}
		}
	}

	/// <summary>
	/// Parses <c>git grep -n --column -z</c> output (<c>path NUL line NUL column NUL text</c> per line) into
	/// matches with UTF-16 columns, capping at <paramref name="cap"/> and flagging truncation. Pure, testable.
	/// </summary>
	public static GrepResult Parse(string output, int cap) {
		ArgumentNullException.ThrowIfNull(output);
		var matches = new List<GrepMatch>();
		bool truncated = false;
		foreach (string raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
			if (matches.Count >= cap) {
				truncated = true;
				break;
			}

			string[] fields = raw.Split('\0', 4);
			if (fields.Length < 4 || !int.TryParse(fields[1], out int line) || !int.TryParse(fields[2], out int byteColumn)) {
				continue;
			}

			string preview = fields[3].TrimEnd('\r'); // a CRLF file's stored line keeps its \r
			matches.Add(new GrepMatch {
				Path = fields[0],
				Line = line,
				Column = Utf16Column(preview, byteColumn),
				Preview = preview,
			});
		}

		return new GrepResult { Matches = matches, Truncated = truncated };
	}

	/// <summary>
	/// Git's 1-based UTF-8 byte column within <paramref name="line"/> as a 1-based UTF-16 column (what JS
	/// strings and Monaco positions count in), clamped to the line's end.
	/// </summary>
	public static int Utf16Column(string line, int byteColumn) {
		ArgumentNullException.ThrowIfNull(line);
		int i = 0;
		int bytes = 0;
		while (i < line.Length && bytes < byteColumn - 1) {
			if (char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) {
				bytes += 4;
				i += 2;
			} else {
				bytes += Encoding.UTF8.GetByteCount(line, i, 1);
				i += 1;
			}
		}

		return i + 1;
	}
}
