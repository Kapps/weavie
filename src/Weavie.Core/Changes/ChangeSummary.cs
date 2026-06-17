namespace Weavie.Core.Changes;

/// <summary>A compact per-file entry for the session changes list: path plus added/removed line counts.</summary>
public sealed record ChangeSummary {
	/// <summary>Absolute path of the changed file.</summary>
	public required string Path { get; init; }

	/// <summary>Lines added since the file's session baseline.</summary>
	public required int Added { get; init; }

	/// <summary>Lines removed since the file's session baseline.</summary>
	public required int Removed { get; init; }
}
