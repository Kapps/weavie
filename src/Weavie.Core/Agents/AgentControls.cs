namespace Weavie.Core.Agents;

/// <summary>One selectable value for a control axis — a model, an approval policy, a sandbox mode.</summary>
public sealed record AgentControlOption {
	/// <summary>The value echoed back to <see cref="IStructuredAgentControls.SetControl"/> when picked.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing label.</summary>
	public required string Label { get; init; }

	/// <summary>Optional one-line description shown under the label.</summary>
	public string? Description { get; init; }
}

/// <summary>One adjustable control on a session (mode, approvals, sandbox) with its current value and choices.</summary>
public sealed record AgentControlAxis {
	/// <summary>The provider-opaque axis key the web echoes back verbatim (e.g. <c>sandbox</c>).</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing axis name, e.g. "Sandbox".</summary>
	public required string Label { get; init; }

	/// <summary>The current option id.</summary>
	public required string Value { get; init; }

	/// <summary>The current option's label, shown in the status line.</summary>
	public required string ValueLabel { get; init; }

	/// <summary>The choices offered when the axis is opened.</summary>
	public required IReadOnlyList<AgentControlOption> Options { get; init; }

	/// <summary>The command that operates this axis, used to advertise its effective keybinding.</summary>
	public string? CommandId { get; init; }
}

/// <summary>One model in the combined model control, with the reasoning efforts and Fast state it offers.</summary>
public sealed record AgentModelChoice {
	/// <summary>The model id echoed back as the <c>model</c> control value.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing model name, e.g. "GPT-5.5".</summary>
	public required string Label { get; init; }

	/// <summary>Whether this is the session's active model.</summary>
	public required bool Current { get; init; }

	/// <summary>The effort id selected for this model — the effective effort when current, else the model default.</summary>
	public required string Effort { get; init; }

	/// <summary>The reasoning efforts this model offers, as options for its submenu.</summary>
	public required IReadOnlyList<AgentControlOption> Efforts { get; init; }

	/// <summary>The service-tier id that turns Fast Mode on for this model, or empty when it offers no Fast tier.</summary>
	public required string FastTier { get; init; }

	/// <summary>Whether Fast Mode is on (only ever true for the active model).</summary>
	public required bool FastOn { get; init; }
}

/// <summary>The merged model → effort / Fast control: one status-line item whose picker opens a per-model submenu.</summary>
public sealed record AgentModelControl {
	/// <summary>The active model id.</summary>
	public required string Value { get; init; }

	/// <summary>The composite status-line label, e.g. "GPT-5.5 (X-High) ⚡".</summary>
	public required string ValueLabel { get; init; }

	/// <summary>The selectable models, each carrying its efforts and Fast state for the submenu.</summary>
	public required IReadOnlyList<AgentModelChoice> Models { get; init; }
}

/// <summary>
/// One slash-menu entry. Exactly one action is set: <see cref="CommandId"/> dispatches a built-in command,
/// <see cref="InsertText"/> inserts text, or <see cref="SkillName"/> stages a provider skill for the next turn.
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

	/// <summary>When set, selecting the entry stages this provider skill, submitted as a structured skill input.</summary>
	public string? SkillName { get; init; }
}

/// <summary>The provider-neutral control + slash surface for one structured-agent session, pushed to the web.</summary>
public sealed record AgentControlState {
	/// <summary>The merged model / effort / Fast control shown first in the status line.</summary>
	public required AgentModelControl ModelControl { get; init; }

	/// <summary>The remaining adjustable control axes (mode, approvals, sandbox) shown in the composer status line.</summary>
	public required IReadOnlyList<AgentControlAxis> Axes { get; init; }

	/// <summary>The slash-menu entries offered when the composer starts with a slash.</summary>
	public required IReadOnlyList<AgentSlashEntry> Slash { get; init; }
}
