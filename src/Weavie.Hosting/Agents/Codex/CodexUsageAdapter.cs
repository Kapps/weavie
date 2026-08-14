using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Hosting.Agents.Codex;

internal sealed record CodexThreadUsage(
	string ThreadId,
	AgentContextWindowUsage? ContextWindow,
	long TotalTokens);

internal sealed record CodexRateLimits(
	string Id,
	string? Label,
	IReadOnlyList<AgentRateLimitUsage> Windows);

internal static class CodexUsageAdapter {
	private const string DefaultLimitId = "codex";

	public static bool TryReadThreadUsage(JsonElement notification, out CodexThreadUsage usage) {
		usage = default!;
		if (!notification.TryGetProperty("params", out var parameters)
			|| parameters.GetStringOrEmpty("threadId") is not { Length: > 0 } threadId
			|| !parameters.TryGetProperty("tokenUsage", out var tokenUsage)
			|| !tokenUsage.TryGetProperty("last", out var last)
			|| !TryReadLong(last, "totalTokens", out long contextTokens)
			|| !tokenUsage.TryGetProperty("total", out var total)
			|| !TryReadLong(total, "totalTokens", out long totalTokens)) {
			return false;
		}

		AgentContextWindowUsage? context = TryReadLong(tokenUsage, "modelContextWindow", out long capacity)
			&& capacity > 0
			? new(contextTokens, capacity)
			: null;
		usage = new(threadId, context, totalTokens);
		return true;
	}

	public static bool TryReadRateLimitSnapshot(JsonElement result, out CodexRateLimits limits) =>
		TryReadRateLimits(result, DefaultLimitId, out limits);

	public static bool TryReadRateLimitUpdate(JsonElement notification, out CodexRateLimits limits) {
		limits = default!;
		return notification.TryGetProperty("params", out var parameters)
			&& TryReadRateLimits(parameters, string.Empty, out limits);
	}

	private static bool TryReadRateLimits(JsonElement parent, string fallbackId, out CodexRateLimits limits) {
		limits = default!;
		if (!parent.TryGetProperty("rateLimits", out var rateLimits)
			|| rateLimits.ValueKind != JsonValueKind.Object) {
			return false;
		}

		string id = rateLimits.GetStringOrEmpty("limitId") is { Length: > 0 } value
			? value
			: fallbackId;
		List<AgentRateLimitUsage> windows = [];
		AddWindow(windows, rateLimits, id, "primary");
		AddWindow(windows, rateLimits, id, "secondary");
		limits = new(id, rateLimits.GetStringOrNull("limitName"), windows);
		return true;
	}

	private static void AddWindow(List<AgentRateLimitUsage> limits, JsonElement bucket, string bucketId, string windowId) {
		if (!bucket.TryGetProperty(windowId, out var value)
			|| value.ValueKind != JsonValueKind.Object
			|| !value.TryGetProperty("usedPercent", out var used)
			|| !used.TryGetDouble(out double usedPercent)
			|| usedPercent < 0) {
			return;
		}

		long? minutes = TryReadLong(value, "windowDurationMins", out long duration) && duration > 0
			? duration
			: null;
		DateTimeOffset? resetsAt = TryReadLong(value, "resetsAt", out long seconds)
			&& seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
			? DateTimeOffset.FromUnixTimeSeconds(seconds)
			: null;
		limits.Add(new($"{bucketId}:{windowId}", null, usedPercent, minutes, resetsAt));
	}

	private static bool TryReadLong(JsonElement value, string name, out long result) {
		result = 0;
		return value.TryGetProperty(name, out var property)
			&& property.TryGetInt64(out result)
			&& result >= 0;
	}
}
