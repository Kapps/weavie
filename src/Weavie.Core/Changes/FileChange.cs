namespace Weavie.Core.Changes;

/// <summary>One file changed during the session: its content at first touch this session vs. now.</summary>
public sealed record FileChange {
	/// <summary>Absolute path of the changed file.</summary>
	public required string Path { get; init; }

	/// <summary>The file's content when it was first touched this session (empty if it was created).</summary>
	public required string BaselineText { get; init; }

	/// <summary>The file's latest content.</summary>
	public required string CurrentText { get; init; }

	/// <summary>Whether the baseline-side file exists, distinct from an absent file with empty content.</summary>
	public required bool BaselineExists { get; init; }

	/// <summary>Whether the current-side file exists, distinct from an existing empty file.</summary>
	public required bool CurrentExists { get; init; }

	/// <summary>
	/// The file's content at the last keep-all (the review's "accepted anchor"), for the inline turn-review's
	/// faded band (accepted anchor → review baseline). Only meaningful on the <see cref="SessionChangeTracker.GetTurn"/>
	/// / <see cref="SessionChangeTracker.TurnChanges"/> triple; defaults to empty for the session-diff views.
	/// </summary>
	public string AcceptedBaselineText { get; init; } = string.Empty;

	/// <summary>Whether the accepted-anchor file exists.</summary>
	public bool AcceptedBaselineExists { get; init; } = true;
}

/// <summary>
/// A turn change plus the counts the review navigator renders, diffed once from the texts in
/// <paramref name="Change"/>.
/// </summary>
/// <param name="Change">The change this summary describes.</param>
/// <param name="Added">Lines added between the accepted anchor and current.</param>
/// <param name="Removed">Lines removed between the accepted anchor and current.</param>
/// <param name="Line">The 1-based line the review walk lands on: the first pending hunk, else the first faded one.</param>
public sealed record TurnChangeSummary(FileChange Change, int Added, int Removed, int Line) {
	internal static TurnChangeSummary For(FileChange change) {
		// Count over the full span (accepted anchor → current) so a fully-kept (faded-only) file still reads as
		// changed; land the walk on the first PENDING hunk, falling back to the first faded one.
		var (added, removed) = LineDiff.Count(change.AcceptedBaselineText, change.CurrentText);
		return new TurnChangeSummary(
			change,
			added,
			removed,
			LineDiff.FirstChangedLine(change.BaselineText, change.CurrentText)
				?? LineDiff.FirstChangedLine(change.AcceptedBaselineText, change.CurrentText)
				?? 1);
	}

	// The texts are the tracker's own instances, so reference equality means "not rediffed since".
	internal bool Describes(FileChange other) =>
		ReferenceEquals(Change.AcceptedBaselineText, other.AcceptedBaselineText)
		&& ReferenceEquals(Change.BaselineText, other.BaselineText)
		&& ReferenceEquals(Change.CurrentText, other.CurrentText)
		&& Change.AcceptedBaselineExists == other.AcceptedBaselineExists
		&& Change.BaselineExists == other.BaselineExists
		&& Change.CurrentExists == other.CurrentExists;
}
