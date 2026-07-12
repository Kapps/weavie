namespace Weavie.Core.Agents;

/// <summary>A provider-neutral update for Weavie's native agent pane.</summary>
public sealed record AgentPaneMessage {
	/// <summary>The web-facing message kind.</summary>
	public required string Type { get; init; }

	/// <summary>The provider id that produced the update.</summary>
	public required string ProviderId { get; init; }

	/// <summary>The current provider thread id, when known.</summary>
	public string? ThreadId { get; init; }

	/// <summary>Whether this update belongs to the session's primary thread; unknown for legacy providers.</summary>
	public bool? IsPrimaryThread { get; init; }

	/// <summary>The current provider turn id, when known.</summary>
	public string? TurnId { get; init; }

	/// <summary>The item id associated with this update, when any.</summary>
	public string? ItemId { get; init; }

	/// <summary>The structured item kind, when any.</summary>
	public string? ItemType { get; init; }

	/// <summary>The provider-neutral presentation category for this update.</summary>
	public string? Category { get; init; }

	/// <summary>A concise user-facing summary for list/card rendering.</summary>
	public string? Summary { get; init; }

	/// <summary>Streaming or final text content, when any.</summary>
	public string? Text { get; init; }

	/// <summary>A status value from the provider contract.</summary>
	public string? Status { get; init; }

	/// <summary>Normalized questions for an input request, when any.</summary>
	public IReadOnlyList<AgentInputQuestion>? Questions { get; init; }

	/// <summary>Raw provider payload for details panes and forward-compatible rendering.</summary>
	public string? PayloadJson { get; init; }
}
