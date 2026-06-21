using System.Text.Json;

namespace Weavie.Core.Json;

/// <summary>
/// Tolerant readers and writers for the hand-built web-message / MCP wire protocols. Each reader returns a
/// fallback when the property is missing or the wrong JSON kind rather than throwing, so a malformed message
/// degrades instead of faulting the dispatch loop. Keeps the same null/kind handling in one place instead of
/// re-inlined at every call site.
/// </summary>
public static class JsonWire {
	/// <summary>The <paramref name="name"/> string property, or empty when absent/null/not a string.</summary>
	public static string GetStringOr(this JsonElement root, string name) => root.GetStringOr(name, string.Empty);

	/// <summary>The <paramref name="name"/> string property, or <paramref name="fallback"/> when absent/null/not a string.</summary>
	public static string GetStringOr(this JsonElement root, string name, string fallback) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
			? el.GetString() ?? fallback
			: fallback;

	/// <summary>The <paramref name="name"/> int property, or 0 when absent/not a number.</summary>
	public static int GetInt32Or(this JsonElement root, string name) => root.GetInt32Or(name, 0);

	/// <summary>The <paramref name="name"/> int property, or <paramref name="fallback"/> when absent/not a number.</summary>
	public static int GetInt32Or(this JsonElement root, string name, int fallback) =>
		root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;

	/// <summary>The <paramref name="name"/> bool property, or false when absent/not a JSON boolean.</summary>
	public static bool GetBoolOr(this JsonElement root, string name) => root.GetBoolOr(name, false);

	/// <summary>The <paramref name="name"/> bool property, or <paramref name="fallback"/> when absent/not a JSON boolean.</summary>
	public static bool GetBoolOr(this JsonElement root, string name, bool fallback) =>
		root.TryGetProperty(name, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
			? el.GetBoolean()
			: fallback;

	/// <summary>A quoted, escaped JSON string literal for hand-built JSON payloads.</summary>
	public static string Quote(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
