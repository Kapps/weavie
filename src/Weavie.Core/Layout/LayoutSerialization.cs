using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Layout;

/// <summary>
/// JSON (de)serialization for <see cref="LayoutDocument"/>: camelCase names, indented output, and the
/// polymorphic node discriminator. The on-disk and wire (web/MCP) formats are identical.
/// </summary>
public static class LayoutSerialization {
	/// <summary>Shared options: camelCase property and enum names, indented output, nulls omitted.</summary>
	public static JsonSerializerOptions Options { get; } = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	// Like Options but not indented — for the host↔web bridge, where the document rides in a single-line
	// message and newlines would have to be escaped.
	private static readonly JsonSerializerOptions CompactOptions = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
	};

	/// <summary>Serializes a document to indented JSON (the on-disk form).</summary>
	public static string Serialize(LayoutDocument document) => JsonSerializer.Serialize(document, Options);

	/// <summary>Serializes a document to compact single-line JSON (the bridge wire form).</summary>
	public static string SerializeCompact(LayoutDocument document) => JsonSerializer.Serialize(document, CompactOptions);

	/// <summary>
	/// Parses a document without throwing. Returns <c>false</c> with an <paramref name="error"/> message on
	/// malformed JSON or a document missing its root.
	/// </summary>
	public static bool TryDeserialize(string json, out LayoutDocument? document, out string? error) {
		try {
			document = JsonSerializer.Deserialize<LayoutDocument>(json, Options);
			if (document?.Root is null) {
				document = null;
				error = "layout document was empty or missing its root";
				return false;
			}

			error = null;
			return true;
		} catch (JsonException ex) {
			document = null;
			error = ex.Message;
			return false;
		}
	}
}
