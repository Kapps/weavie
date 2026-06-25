namespace Weavie.Core.Changes;

/// <summary>One file changed during the session: its content at first touch this session vs. now.</summary>
public sealed record FileChange {
	/// <summary>Absolute path of the changed file.</summary>
	public required string Path { get; init; }

	/// <summary>The file's content when it was first touched this session (empty if it was created).</summary>
	public required string BaselineText { get; init; }

	/// <summary>The file's latest content.</summary>
	public required string CurrentText { get; init; }

	/// <summary>
	/// The file's content at the last keep-all (the review's "accepted anchor"), for the inline turn-review's
	/// faded band (accepted anchor → review baseline). Only meaningful on the <see cref="SessionChangeTracker.GetTurn"/>
	/// / <see cref="SessionChangeTracker.TurnChanges"/> triple; defaults to empty for the session-diff views.
	/// </summary>
	public string AcceptedBaselineText { get; init; } = string.Empty;
}
