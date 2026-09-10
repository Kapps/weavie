using System.Text;

namespace Weavie.Core.Agents;

/// <summary>Closes process-owned activity left in a saved display when its host stopped.</summary>
public static class AgentPaneRecovery {
	/// <summary>Returns explicit interruption events, preserving partial output and resolved requests.</summary>
	public static IReadOnlyList<AgentPaneMessage> Interrupt(IReadOnlyList<AgentPaneMessage> messages) {
		var openings = new Dictionary<string, AgentPaneMessage>(StringComparer.Ordinal);
		var turns = new Dictionary<(string?, string?, string?), AgentPaneMessage>();
		var items = new Dictionary<string, (AgentPaneMessage Message, StringBuilder Text)>();
		var requests = new Dictionary<(string?, string?), AgentPaneMessage>();
		foreach (var message in messages) {
			if (message.ConversationId is { } side) {
				if (message.Type == "side-conversation-started" && message.Status == "forking") openings[side] = message;
				if (message.Type is "turn-started" or "turn-completed" or "side-conversation-failed") openings.Remove(side);
			}
			var turnKey = (message.ConversationId, message.ThreadId, message.TurnId);
			if (message.Type == "turn-started") turns[turnKey] = message;
			if (message.Type == "turn-completed") turns.Remove(turnKey);
			if (message.RequestId is not null) {
				var requestKey = (message.ConversationId, message.RequestId);
				if (message.Type.EndsWith("-requested", StringComparison.Ordinal)) requests[requestKey] = message;
				if (message.Type.EndsWith("-resolved", StringComparison.Ordinal)) requests.Remove(requestKey);
			}
			string? key = AgentPaneIdentity.ItemKey(message);
			if (key is null) continue;
			if (message.Type is "item-completed" or "item-retracted") items.Remove(key);
			else if (message.Type == "item-started") items[key] = (message, new StringBuilder(message.Text));
			else if (message.Type is "agent-message-delta" or "thought-message-delta" or "plan-delta" or "command-output-delta") {
				var buffer = items.TryGetValue(key, out var existing) ? existing.Text : new StringBuilder();
				buffer.Append(message.Text);
				items[key] = (message, buffer);
			}
		}
		return [
			.. openings.Values.Select(message => message with {
				Type = "turn-completed", Status = "cancelled", Text = null,
				Summary = "The initial side question was interrupted when the session stopped.",
			}),
			.. items.Values.Select(item => item.Message with {
				Type = "item-completed", Text = item.Text.ToString(), Status = "cancelled",
			}),
			.. requests.Values.Select(message => message with {
				Type = message.Type.Replace("-requested", "-resolved", StringComparison.Ordinal),
				Status = "cancelled",
			}),
			.. turns.Values.Select(message => message with {
				Type = "turn-completed", Status = "cancelled", Summary = "Interrupted when the session stopped.",
			}),
		];
	}
}
