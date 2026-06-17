namespace Weavie.Core.Changes;

/// <summary>One file changed during the session: its content at first touch this session vs. now.</summary>
public sealed record FileChange {
	/// <summary>Absolute path of the changed file.</summary>
	public required string Path { get; init; }

	/// <summary>The file's content when it was first touched this session (empty if it was created).</summary>
	public required string BaselineText { get; init; }

	/// <summary>The file's latest content.</summary>
	public required string CurrentText { get; init; }
}
