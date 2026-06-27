namespace Weavie.Core.Git;

/// <summary>One file changed between two refs, with its line-count delta — an entry in a PR's changed-file list.</summary>
public sealed record DiffFileChange {
	/// <summary>The file's repository-relative path (forward slashes, as git emits).</summary>
	public required string Path { get; init; }

	/// <summary>Lines added (0 for a binary file, which git reports as <c>-</c>).</summary>
	public required int Added { get; init; }

	/// <summary>Lines removed (0 for a binary file).</summary>
	public required int Removed { get; init; }
}
