namespace Weavie.Core.Git;

/// <summary>One content-search hit: a file, the 1-based line, and the matched line's text (the preview).</summary>
public sealed record GrepMatch {
	/// <summary>The file's repository-relative path (forward slashes, as git emits).</summary>
	public required string Path { get; init; }

	/// <summary>The 1-based line number of the match.</summary>
	public required int Line { get; init; }

	/// <summary>The 1-based UTF-16 column of the line's first match (converted from git's byte offset).</summary>
	public required int Column { get; init; }

	/// <summary>The full matched line's text (the result preview).</summary>
	public required string Preview { get; init; }
}

/// <summary>How a content search matches (see <see cref="GitService.GrepAsync"/>): query semantics plus which paths are searched.</summary>
public sealed record GrepOptions {
	/// <summary>Match case exactly; off searches case-insensitively (<c>git grep -i</c>).</summary>
	public required bool CaseSensitive { get; init; }

	/// <summary>Match whole words only (<c>git grep -w</c>).</summary>
	public required bool WholeWord { get; init; }

	/// <summary>Treat the query as a POSIX extended regex (<c>-E</c>) instead of a literal string (<c>-F</c>).</summary>
	public required bool Regex { get; init; }

	/// <summary>Comma-separated path globs to search (e.g. <c>*.ts, src/</c>); empty searches everything.</summary>
	public required string Include { get; init; }

	/// <summary>Comma-separated path globs to skip (each becomes a <c>:(exclude)</c> pathspec); empty skips nothing.</summary>
	public required string Exclude { get; init; }

	/// <summary>Skip gitignored files (the git-grep default); off searches them too (<c>--no-exclude-standard</c>).</summary>
	public required bool ExcludeGitignored { get; init; }
}

/// <summary>The result of a content search: its matches and whether they were capped (more existed than returned).</summary>
public sealed record GrepResult {
	/// <summary>The matches, in git's order (grouped by file).</summary>
	public required IReadOnlyList<GrepMatch> Matches { get; init; }

	/// <summary>True when the match cap was hit, so the list is incomplete — surfaced to the user, never dropped silently.</summary>
	public required bool Truncated { get; init; }
}
