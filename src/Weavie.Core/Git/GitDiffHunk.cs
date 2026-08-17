using System.Globalization;

namespace Weavie.Core.Git;

/// <summary>One hunk of a unified diff, kept as raw body lines so the renderer owns presentation.</summary>
/// <param name="Header">The hunk's <c>@@</c> line, section heading included.</param>
/// <param name="OldStart">The first line number the hunk covers in the pre-image.</param>
/// <param name="NewStart">The first line number the hunk covers in the post-image.</param>
/// <param name="Lines">The body lines, each keeping its <c>' '</c>, <c>'+'</c>, <c>'-'</c>, or <c>'\'</c> marker.</param>
public sealed record GitDiffHunk(string Header, int OldStart, int NewStart, IReadOnlyList<string> Lines);

/// <summary>
/// Reads unified-diff text. Body lines are consumed against the counts the <c>@@</c> header declares rather than
/// by sniffing prefixes, so file content that itself looks like a diff marker cannot end a hunk early. Pure, so
/// it is tested without a repository.
/// </summary>
public static class UnifiedDiff {
	/// <summary>
	/// The hunk whose post-image covers <paramref name="newLine"/>, or <c>null</c> when no hunk does (the commit
	/// did not touch that line, or touched the file only by deletion).
	/// </summary>
	public static GitDiffHunk? HunkContaining(string diff, int newLine) {
		ArgumentNullException.ThrowIfNull(diff);
		foreach (var hunk in Hunks(diff)) {
			int newCount = hunk.Lines.Count(line => line.Length == 0 || line[0] is ' ' or '+');
			if (newLine >= hunk.NewStart && newLine < hunk.NewStart + newCount) {
				return hunk;
			}
		}

		return null;
	}

	/// <summary>
	/// The post-image start line of a <c>@@</c> header, for reading a line's number out of a patch without
	/// collecting its body. False for any other line.
	/// </summary>
	public static bool TryParseNewStart(string line, out int newStart) {
		ArgumentNullException.ThrowIfNull(line);
		bool parsed = TryParseHunkHeader(line, out _, out _, out newStart, out _);
		return parsed && newStart > 0;
	}

	/// <summary>Every hunk in <paramref name="diff"/>, in order.</summary>
	public static IReadOnlyList<GitDiffHunk> Hunks(string diff) {
		ArgumentNullException.ThrowIfNull(diff);
		string[] lines = diff.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
		var hunks = new List<GitDiffHunk>();
		for (int i = 0; i < lines.Length; i++) {
			if (!TryParseHunkHeader(lines[i], out int oldStart, out int oldCount, out int newStart, out int newCount)) {
				continue;
			}

			var body = new List<string>();
			while (++i < lines.Length && (oldCount > 0 || newCount > 0)) {
				string line = lines[i];
				// A "\ No newline at end of file" marker belongs to the hunk but consumes neither side's budget.
				if (line.StartsWith('\\')) {
					body.Add(line);
					continue;
				}

				char marker = line.Length == 0 ? ' ' : line[0];
				if (marker is not (' ' or '+' or '-')) {
					break;
				}

				oldCount -= marker is ' ' or '-' ? 1 : 0;
				newCount -= marker is ' ' or '+' ? 1 : 0;
				body.Add(line);
			}

			hunks.Add(new GitDiffHunk(lines[i - body.Count - 1], oldStart, newStart, body));
			i--;
		}

		return hunks;
	}

	// "@@ -oldStart,oldCount +newStart,newCount @@ optional section heading"; a count of 1 may be omitted.
	private static bool TryParseHunkHeader(string line, out int oldStart, out int oldCount, out int newStart, out int newCount) {
		oldStart = oldCount = newStart = newCount = 0;
		if (!line.StartsWith("@@ ", StringComparison.Ordinal)) {
			return false;
		}

		int close = line.IndexOf(" @@", StringComparison.Ordinal);
		if (close < 0) {
			return false;
		}

		string[] ranges = line[3..close].Split(' ');
		return ranges.Length == 2
			&& TryParseRange(ranges[0], '-', out oldStart, out oldCount)
			&& TryParseRange(ranges[1], '+', out newStart, out newCount);
	}

	private static bool TryParseRange(string text, char sign, out int start, out int count) {
		start = 0;
		count = 0;
		if (text.Length < 2 || text[0] != sign) {
			return false;
		}

		string[] parts = text[1..].Split(',');
		count = 1;
		return int.TryParse(parts[0], CultureInfo.InvariantCulture, out start)
			&& (parts.Length == 1 || int.TryParse(parts[1], CultureInfo.InvariantCulture, out count));
	}
}
