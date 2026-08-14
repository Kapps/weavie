namespace Weavie.Core.Agents;

/// <summary>One selectable value for a provider-owned session configuration option.</summary>
public sealed record AgentControlOption {
	/// <summary>The value echoed back to <see cref="IStructuredAgentControls.SetControl"/> when picked.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing label.</summary>
	public required string Label { get; init; }

	/// <summary>Optional one-line description shown under the label.</summary>
	public string? Description { get; init; }

	/// <summary>Optional provider-owned group label for clustered selector values.</summary>
	public string? Group { get; init; }
}

/// <summary>One ordered provider-owned session configuration option.</summary>
public sealed record AgentControlAxis {
	/// <summary>The provider-opaque axis key the web echoes back verbatim (e.g. <c>sandbox</c>).</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing axis name, e.g. "Sandbox".</summary>
	public required string Label { get; init; }

	/// <summary>The optional provider description shown with the control.</summary>
	public string? Description { get; init; }

	/// <summary>The provider's semantic category, used only for presentation ordering.</summary>
	public string? Category { get; init; }

	/// <summary>The option shape: <c>select</c> or <c>boolean</c>.</summary>
	public required string Kind { get; init; }

	/// <summary>The current option id.</summary>
	public required string Value { get; init; }

	/// <summary>The current option's label, shown in the status line.</summary>
	public required string ValueLabel { get; init; }

	/// <summary>The choices offered when the axis is opened.</summary>
	public required IReadOnlyList<AgentControlOption> Options { get; init; }

}

/// <summary>
/// One slash-menu entry. <see cref="CommandId"/> dispatches a Weavie command; otherwise
/// <see cref="InsertText"/> inserts the provider command into the composer.
/// </summary>
public sealed record AgentSlashEntry {
	/// <summary>A stable id, unique within the menu.</summary>
	public required string Id { get; init; }

	/// <summary>The name shown after the leading slash, e.g. "model" or a skill name.</summary>
	public required string Name { get; init; }

	/// <summary>A one-line description shown beside the name.</summary>
	public required string Description { get; init; }

	/// <summary>When set, selecting the entry dispatches this Weavie command.</summary>
	public string? CommandId { get; init; }

	/// <summary>When set, selecting the entry replaces the slash query with this text.</summary>
	public string? InsertText { get; init; }

}

/// <summary>The provider-neutral control + slash surface for one structured-agent session, pushed to the web.</summary>
public sealed record AgentControlState {
	/// <summary>The provider-owned configuration options, in the provider's order.</summary>
	public required IReadOnlyList<AgentControlAxis> Axes { get; init; }

	/// <summary>The slash-menu entries offered when the composer starts with a slash.</summary>
	public required IReadOnlyList<AgentSlashEntry> Slash { get; init; }
}
