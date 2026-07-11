using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Serializes provider-neutral agent control state (model / approvals / sandbox / slash) for the web bridge.</summary>
internal static class AgentControlsProtocol {
	public static string Message(string slot, string workspace, AgentControlState state) {
		ArgumentNullException.ThrowIfNull(slot);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(state);
		return JsonSerializer.Serialize(new {
			type = "agent-controls",
			slot,
			workspace,
			state = new {
				axes = state.Axes.Select(axis => new {
					id = axis.Id,
					label = axis.Label,
					value = axis.Value,
					valueLabel = axis.ValueLabel,
					options = axis.Options.Select(option => new {
						id = option.Id,
						label = option.Label,
						description = option.Description,
					}),
				}),
				slash = state.Slash.Select(entry => new {
					id = entry.Id,
					name = entry.Name,
					description = entry.Description,
					commandId = entry.CommandId,
					insertText = entry.InsertText,
					skillName = entry.SkillName,
				}),
			},
		});
	}
}
