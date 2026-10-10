using System.Text.Json;

namespace Weavie.AgentClientProtocol;

/// <summary>Strict readers for ACP JSON payloads; violations are protocol failures.</summary>
internal static class AcpJson {
	public static string RequiredString(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The {source} is missing '{property}'.");

	public static string RequiredText(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			? result.GetString()!
			: throw new AcpProtocolException($"The {source} is missing string '{property}'.");

	public static string? OptionalString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			? result.GetString()
			: null;

	public static int? ReadOptionalNonNegativeInt(JsonElement value, string property) {
		if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (!result.TryGetInt32(out int number) || number < 0) {
			throw new AcpProtocolException($"'{property}' must be a non-negative integer.");
		}
		return number;
	}

	public static double? ReadOptionalDouble(JsonElement value, string property) {
		if (!value.TryGetProperty(property, out var result) || result.ValueKind == JsonValueKind.Null) {
			return null;
		}
		if (!result.TryGetDouble(out double number) || !double.IsFinite(number)) {
			throw new AcpProtocolException($"'{property}' must be a finite number.");
		}
		return number;
	}

	/// <summary>The subagent a <c>subagent_spawned</c> update announces, or null for any other notification.</summary>
	public static string? SpawnedSubagent(JsonElement notification) =>
		OptionalString(notification, "method") == "session/update"
			&& notification.TryGetProperty("params", out var parameters)
			&& parameters.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Object
			&& OptionalString(update, "sessionUpdate") == "subagent_spawned"
				? RequiredString(update, "subagentSessionId", "subagent_spawned update")
				: null;

	public static long ReadRequiredNonNegativeInt64(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.TryGetInt64(out long number) && number >= 0
			? number
			: throw new AcpProtocolException($"The ACP {source} requires a non-negative '{property}'.");
}
