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

/// <summary>The semantic action owned by one slash-menu entry.</summary>
public enum AgentSlashEntryKind {
	/// <summary>A Weavie command handled by Core or the web client.</summary>
	WeavieCommand,

	/// <summary>A command from the provider's latest ACP command catalog.</summary>
	ProviderCommand,
}

/// <summary>One typed slash-menu action.</summary>
public sealed record AgentSlashEntry {
	/// <summary>A stable id, unique within the menu.</summary>
	public required string Id { get; init; }

	/// <summary>The name shown after the leading slash, e.g. "model" or a skill name.</summary>
	public required string Name { get; init; }

	/// <summary>A one-line description shown beside the name.</summary>
	public required string Description { get; init; }

	/// <summary>Whether accepting the row dispatches Weavie or invokes the provider command.</summary>
	public required AgentSlashEntryKind Kind { get; init; }

	/// <summary>The command dispatched for a <see cref="AgentSlashEntryKind.WeavieCommand"/> entry.</summary>
	public string? CommandId { get; init; }

	/// <summary>The provider's optional hint for the command's unstructured input.</summary>
	public string? InputHint { get; init; }

	/// <summary>The command argument populated from free-form input for a Weavie-owned entry.</summary>
	public string? InputName { get; init; }
}

/// <summary>The provider-neutral control + slash surface for one structured-agent session, pushed to the web.</summary>
public sealed record AgentControlState {
	/// <summary>The provider-owned configuration options, in the provider's order.</summary>
	public required IReadOnlyList<AgentControlAxis> Axes { get; init; }

	/// <summary>The slash-menu entries offered when the composer starts with a slash.</summary>
	public required IReadOnlyList<AgentSlashEntry> Slash { get; init; }
}

/// <summary>Composes Weavie-owned actions with the provider's authoritative slash-command snapshot.</summary>
public static class AgentControlCommands {
	/// <summary>The client-owned command that abandons the current provider conversation and starts fresh.</summary>
	public static AgentSlashEntry ClearConversation { get; } = new() {
		Id = "weavie:clear",
		Name = "clear",
		Description = "Clear the transcript and start a fresh conversation",
		Kind = AgentSlashEntryKind.WeavieCommand,
		CommandId = Commands.CoreCommands.ClearAgentConversation,
	};

	/// <summary>Asks a context-preserving question outside the primary provider transcript.</summary>
	public static AgentSlashEntry AskAside { get; } = new() {
		Id = "weavie:btw",
		Name = "btw",
		Description = "Ask from the current context without adding to the main conversation",
		Kind = AgentSlashEntryKind.WeavieCommand,
		CommandId = Commands.CoreCommands.AskAgentAside,
		InputHint = "question",
		InputName = "question",
	};

	/// <summary>Adds built-ins to one provider snapshot, with Weavie semantics winning name collisions.</summary>
	public static IReadOnlyList<AgentSlashEntry> ComposeSlash(
		IReadOnlyList<AgentSlashEntry> providerCommands,
		bool supportsSideConversations) {
		ArgumentNullException.ThrowIfNull(providerCommands);
		return [
			ClearConversation,
			.. supportsSideConversations ? [AskAside] : Array.Empty<AgentSlashEntry>(),
			.. providerCommands.Where(entry =>
				!string.Equals(entry.Name, "clear", StringComparison.OrdinalIgnoreCase)
				&& (!supportsSideConversations
					|| !string.Equals(entry.Name, "btw", StringComparison.OrdinalIgnoreCase))),
		];
	}
}
