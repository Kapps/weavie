using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents;

/// <summary>Builds provider-neutral agent control state (model / effort / Fast / mode / permissions / slash).</summary>
internal static class AgentControlsProtocol {
	public static object Message(AgentControlState state) {
		ArgumentNullException.ThrowIfNull(state);
		return new {
			state = new {
				axes = state.Axes.Select(axis => new {
					id = axis.Id,
					label = axis.Label,
					description = axis.Description,
					category = axis.Category,
					kind = axis.Kind,
					value = axis.Value,
					valueLabel = axis.ValueLabel,
					options = axis.Options.Select(option => new {
						id = option.Id,
						label = option.Label,
						description = option.Description,
						group = option.Group,
					}),
				}),
				slash = state.Slash.Select(entry => new {
					id = entry.Id,
					name = entry.Name,
					description = entry.Description,
					commandId = entry.CommandId,
					insertText = entry.InsertText,
				}),
			},
		};
	}
}
