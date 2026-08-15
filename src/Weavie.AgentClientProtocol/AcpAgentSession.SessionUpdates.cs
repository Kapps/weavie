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
		var reported = ReadUsageLimit(update);
		AgentUsageSnapshot snapshot;
		lock (_gate) {
			_contextUsage = new(used, size);
			if (reported is { } limit) {
				_usageLimits[limit.Id] = limit;
			}
			snapshot = new(_contextUsage, [.. _usageLimits.Values]);
		}
		UsageChanged?.Invoke(snapshot);
	}

	// Usage windows ride a vendor _meta extension, not the ACP schema: Claude's adapter reports one window
	// per event, so windows accumulate by id rather than replacing each other.
	private static AgentUsageLimit? ReadUsageLimit(JsonElement update) {
		if (!update.TryGetProperty("_meta", out var meta)
			|| !meta.TryGetProperty("_claude/rateLimit", out var limit)
			|| limit.ValueKind != JsonValueKind.Object) {
			return null;
		}
		var status = RequiredString(limit, "status", "usage limit") switch {
			"allowed" => AgentUsageLimitStatus.Allowed,
			"allowed_warning" => AgentUsageLimitStatus.Warning,
			"rejected" => AgentUsageLimitStatus.Exhausted,
			var value => throw new AcpProtocolException($"Unsupported usage-limit status '{value}'."),
		};
		// Claude reports utilization as a 0-1 fraction, and only once a warning threshold is crossed.
		double? usedPercent = limit.TryGetProperty("utilization", out var utilization)
			&& utilization.ValueKind == JsonValueKind.Number
			? utilization.GetDouble() * 100
			: null;
		DateTimeOffset? resetsAt = limit.TryGetProperty("resetsAt", out var resets)
			&& resets.TryGetInt64(out long seconds)
			? DateTimeOffset.FromUnixTimeSeconds(seconds)
			: null;
		return new(OptionalString(limit, "rateLimitType") ?? "limit", status, usedPercent, resetsAt);
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
