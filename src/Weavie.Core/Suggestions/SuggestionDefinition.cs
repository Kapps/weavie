using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Suggestions;

/// <summary>
/// What an action does when the user clicks it.
/// </summary>
public enum SuggestionActionKind {
	/// <summary>Dispatch <see cref="SuggestionAction.CommandId"/>; taking the offer is engagement, so the card also snoozes.</summary>
	RunCommand,

	/// <summary>Hide for this app run ("not now"); a fresh run re-offers.</summary>
	Snooze,

	/// <summary>Dismiss permanently for this workspace ("don't ask again").</summary>
	DismissForever,
}

/// <summary>
/// One button on a suggestion card.
/// </summary>
public sealed record SuggestionAction {
	/// <summary>The button label, e.g. "Yes".</summary>
	public required string Label { get; init; }

	/// <summary>What clicking it does.</summary>
	public required SuggestionActionKind Kind { get; init; }

	/// <summary>The command id to dispatch; set iff <see cref="Kind"/> is <see cref="SuggestionActionKind.RunCommand"/>.</summary>
	public string? CommandId { get; init; }

	/// <summary>Optional raw-JSON argument object passed to the command.</summary>
	public string? ArgsJson { get; init; }
}

/// <summary>
/// The read-only context a suggestion's <see cref="SuggestionDefinition.IsRelevant"/> predicate sees.
/// </summary>
public sealed record SuggestionContext {
	/// <summary>The workspace's root directory.</summary>
	public required string WorkspaceRoot { get; init; }

	/// <summary>The resolved settings, for predicates that key off a setting value.</summary>
	public required SettingsStore Settings { get; init; }

	/// <summary>The filesystem seam, for predicates that probe the workspace.</summary>
	public required IFileSystem FileSystem { get; init; }

	/// <summary>
	/// Whether the workspace has a recognizable dependency/build manifest. Computed once off the hot path by
	/// <see cref="SuggestionService"/> (fail-open on timeout) so the predicate stays a cheap synchronous read.
	/// </summary>
	public required bool HasBuildManifest { get; init; }

	/// <summary>
	/// How many corrections the workspace's ring holds right now. Unlike the one-shot manifest probe this
	/// changes over time, so <see cref="SuggestionService"/> reads it fresh from a supplier each evaluation.
	/// </summary>
	public required int PendingCorrectionCount { get; init; }
}

/// <summary>
/// A declared contextual suggestion: a dismissible nudge evaluated against the current workspace and rendered
/// as a card the user can act on or dismiss. See <c>docs/specs/suggestions.md</c>.
/// </summary>
public sealed record SuggestionDefinition {
	/// <summary>The stable id, unique within the registry, e.g. <c>worktree.setupCommand</c>.</summary>
	public required string Id { get; init; }

	/// <summary>The card headline.</summary>
	public required string Title { get; init; }

	/// <summary>A one-line explanation under the title.</summary>
	public required string Body { get; init; }

	/// <summary>Pure predicate over the context — whether the card should show right now. No side effects, no model calls.</summary>
	public required Func<SuggestionContext, bool> IsRelevant { get; init; }

	/// <summary>The action buttons on the card.</summary>
	public required IReadOnlyList<SuggestionAction> Actions { get; init; }

	/// <summary>Prior ids this suggestion superseded; a persisted "don't ask again" for any of them also dismisses this one, so a renamed card doesn't re-nag users who already dismissed it.</summary>
	public IReadOnlyList<string> LegacyIds { get; init; } = [];
}
