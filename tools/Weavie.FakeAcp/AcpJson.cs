using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

/// <summary>Strict JSON helpers used by the deterministic ACP test agent.</summary>
public static class AcpJson {
	/// <summary>Returns an empty JSON object.</summary>
	public static JsonObject EmptyObject() => [];

	/// <summary>Clones a parsed value into a mutable JSON node.</summary>
	public static JsonNode Clone(JsonElement value) =>
		JsonNode.Parse(value.GetRawText()) ?? throw new JsonException("A JSON value cannot be null here.");

	/// <summary>Reads a required non-empty string property.</summary>
	public static string RequiredString(JsonElement value, string property, string owner) =>
		value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.String
		&& result.GetString() is { Length: > 0 } text
			? text
			: throw AcpAdapterException.InvalidParams($"{owner} requires a non-empty '{property}'.");

	/// <summary>Reads an optional string property.</summary>
	public static string? OptionalString(JsonElement value, string property) =>
		value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.String
			? result.GetString()
			: null;

	/// <summary>Reads a required object property.</summary>
	public static JsonElement RequiredObject(JsonElement value, string property, string owner) =>
		value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.Object
			? result
			: throw AcpAdapterException.InvalidParams($"{owner} requires an object '{property}'.");

	/// <summary>Reads a required array property.</summary>
	public static JsonElement RequiredArray(JsonElement value, string property, string owner) =>
		value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.Array
			? result
			: throw AcpAdapterException.InvalidParams($"{owner} requires an array '{property}'.");

	/// <summary>Returns a culture-invariant JSON-RPC id key.</summary>
	public static string IdKey(JsonElement id) => id.ValueKind switch {
		JsonValueKind.String => "s:" + (id.GetString() ?? string.Empty),
		JsonValueKind.Number when id.TryGetInt64(out long number) => "n:" + number.ToString(CultureInfo.InvariantCulture),
		_ => throw new JsonException("JSON-RPC ids must be strings or signed 64-bit integers."),
	};
}
