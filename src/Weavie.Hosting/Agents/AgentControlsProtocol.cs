using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Serializes provider-neutral agent control state (model / effort / Fast / mode / permissions / slash) for the web bridge.</summary>
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
				modelControl = new {
					value = state.ModelControl.Value,
					valueLabel = state.ModelControl.ValueLabel,
					models = state.ModelControl.Models.Select(model => new {
						id = model.Id,
						label = model.Label,
						current = model.Current,
						effort = model.Effort,
						efforts = model.Efforts.Select(option => new {
							id = option.Id,
							label = option.Label,
							description = option.Description,
						}),
						fastTier = model.FastTier,
						fastOn = model.FastOn,
					}),
				},
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
					commandId = axis.CommandId,
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
