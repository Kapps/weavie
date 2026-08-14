using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void EmitPlan(JsonElement update) {
		string turnId = TurnIdForUpdate(userMessage: false);
		const string itemId = "plan:current";
		if (!update.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("An ACP plan update is missing entries.");
		}
		var lines = entries.EnumerateArray().Select(entry => {
			string status = RequiredString(entry, "status", "plan entry");
			if (status is not ("pending" or "in_progress" or "completed")) {
				throw new AcpProtocolException($"Unsupported ACP plan status '{status}'.");
			}
			string priority = RequiredString(entry, "priority", "plan entry");
			if (priority is not ("high" or "medium" or "low")) {
				throw new AcpProtocolException($"Unsupported ACP plan priority '{priority}'.");
			}
			string marker = status switch {
				"completed" => "[x]",
				"in_progress" => "[~]",
				_ => "[ ]",
			};
			return $"- {marker} {RequiredString(entry, "content", "plan entry")}";
		});
		PublishPane(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Category = "plan",
			Summary = "Plan",
			Text = string.Join('\n', lines),
			Status = "updated",
		});
	}

	private void EmitSessionInfo(JsonElement update) {
		if (OptionalString(update, "title") is { Length: > 0 } title) {
			PublishPane(new AgentPaneMessage {
				Type = "session-info",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				Summary = title,
			});
		}
	}

	private void EmitUsage(JsonElement update) {
		long used = ReadRequiredNonNegativeInt64(update, "used", "usage update");
		long size = ReadRequiredNonNegativeInt64(update, "size", "usage update");
		AgentUsageState state;
		lock (_gate) {
			_usageState = new AgentUsageState(new AgentContextWindowUsage(used, size), null, []);
			state = _usageState;
		}
		UsageStateChanged?.Invoke(state);
		PublishPane(new AgentPaneMessage {
			Type = "usage",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			UsageUsed = used,
			UsageSize = size,
		});
	}

	private void PublishPane(AgentPaneMessage message) {
		lock (_gate) {
			if (_loadingTranscript) {
				_loadedMessages.Add(message);
				return;
			}
		}
		Emit(message);
	}

	private static long ReadRequiredNonNegativeInt64(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.TryGetInt64(out long number) && number >= 0
			? number
			: throw new AcpProtocolException($"The ACP {source} requires a non-negative '{property}'.");
}
