using System.Text.Json;

namespace Weavie.Core.Sources;

/// <summary>Small JSON-reading helpers shared by the source parsers (the "string property or empty" idiom).</summary>
internal static class SourceJson {
	/// <summary>The string value of <paramref name="name"/> on <paramref name="element"/>, or empty when absent/non-string.</summary>
	public static string String(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
