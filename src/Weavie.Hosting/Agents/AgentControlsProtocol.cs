using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds provider-neutral agent control state (model / effort / Fast / mode / permissions / slash).</summary>
internal static class AgentControlsProtocol {
	public static AgentControlsMessage Message(AgentControlState state) {
		ArgumentNullException.ThrowIfNull(state);
		return new(new(
			state.Ready,
			state.Rewindable,
			state.Axes,
			[.. state.Slash.Select(entry => new AgentSlashWire(
				entry.Id,
				entry.Name,
				entry.Description,
				entry.Kind switch {
					AgentSlashEntryKind.WeavieCommand => "weavieCommand",
					AgentSlashEntryKind.ProviderCommand => "providerCommand",
					AgentSlashEntryKind.McpPrompt => "mcpPrompt",
					_ => throw new InvalidOperationException($"Unknown slash entry kind '{entry.Kind}'."),
				},
				entry.CommandId,
				entry.InputHint,
				entry.InputName))]));
	}
}

internal sealed record AgentControlsMessage(AgentControlsState State);

internal sealed record AgentControlsState(
	bool Ready,
	bool Rewindable,
	IReadOnlyList<AgentControlAxis> Axes,
	IReadOnlyList<AgentSlashWire> Slash);

internal sealed record AgentSlashWire(
	string Id,
	string Name,
	string? Description,
	string Kind,
	string? CommandId,
	string? InputHint,
	string? InputName);
