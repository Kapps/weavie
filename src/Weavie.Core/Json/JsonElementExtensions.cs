using System.Text.Json;

namespace Weavie.Core.Json;

/// <summary>
/// Lenient readers for the optional properties of a parsed message/argument object. Every host web-message
/// and every MCP tool-call argument arrives as a <see cref="JsonElement"/> whose fields may be absent or the
/// wrong kind; these return a fallback instead of throwing so callers stay one-liners. Required fields should
/// still use <see cref="JsonElement.GetProperty(string)"/> directly.
/// </summary>
public static class JsonElementExtensions {
	/// <summary>The string value of <paramref name="name"/>, or empty if absent/not a string.</summary>
	public static string GetStringOrEmpty(this JsonElement element, string name) =>
		element.GetStringOrNull(name) ?? string.Empty;

	/// <summary>The string value of <paramref name="name"/>, or <c>null</c> if absent/not a string.</summary>
	public static string? GetStringOrNull(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	/// <summary>The boolean value of <paramref name="name"/>, or <c>false</c> if absent/not a boolean.</summary>
	public static bool GetBoolOrFalse(this JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
		&& value.GetBoolean();

	/// <summary>The int value of <paramref name="name"/>, or <paramref name="fallback"/> if absent/not a number.</summary>
	public static int GetIntOr(this JsonElement element, string name, int fallback) =>
		element.ValueKind == JsonValueKind.Object
		&& element.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.Number
		&& value.TryGetInt32(out int parsed)
			? parsed
			: fallback;
}
