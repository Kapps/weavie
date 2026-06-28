namespace Weavie.Core.Git;

/// <summary>One content-search hit: a file, the 1-based line, and the matched line's text (the preview).</summary>
public sealed record GrepMatch {
	/// <summary>The file's repository-relative path (forward slashes, as git emits).</summary>
	public required string Path { get; init; }

	/// <summary>The 1-based line number of the match.</summary>
	public required int Line { get; init; }

	/// <summary>The full matched line's text (the result preview).</summary>
	public required string Preview { get; init; }
}

/// <summary>The result of a content search: its matches and whether they were capped (more existed than returned).</summary>
public sealed record GrepResult {
	/// <summary>The matches, in git's order (grouped by file).</summary>
	public required IReadOnlyList<GrepMatch> Matches { get; init; }

	/// <summary>True when the match cap was hit, so the list is incomplete — surfaced to the user, never dropped silently.</summary>
	public required bool Truncated { get; init; }
}
