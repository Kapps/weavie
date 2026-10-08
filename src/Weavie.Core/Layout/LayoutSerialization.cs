using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Layout;

/// <summary>
/// JSON (de)serialization for <see cref="LayoutDocument"/>: camelCase names, indented output, and the
/// polymorphic node discriminator. The on-disk and wire (web/MCP) formats are identical.
/// </summary>
public static partial class LayoutSerialization {
	/// <summary>Serializes a document to indented JSON (the on-disk form).</summary>
	public static string Serialize(LayoutDocument document) => JsonSerializer.Serialize(document, LayoutDiskJson.Default.LayoutDocument);

	/// <summary>Serializes a document to compact single-line JSON (the bridge wire form).</summary>
	public static string SerializeCompact(LayoutDocument document) => JsonSerializer.Serialize(document, LayoutWireJson.Default.LayoutDocument);

	/// <summary>Serializes one layout subtree in the on-disk form.</summary>
	public static string SerializeNode(LayoutNode node) => JsonSerializer.Serialize(node, LayoutDiskJson.Default.LayoutNode);

	/// <summary>Parses one layout subtree in the on-disk form.</summary>
	public static LayoutNode? DeserializeNode(string json) => JsonSerializer.Deserialize(json, LayoutDiskJson.Default.LayoutNode);

	/// <summary>
	/// Parses a document without throwing. Returns <c>false</c> with an <paramref name="error"/> message on
	/// malformed JSON or a document missing its root.
	/// </summary>
	public static bool TryDeserialize(string json, out LayoutDocument? document, out string? error) {
		try {
			document = JsonSerializer.Deserialize(json, LayoutDiskJson.Default.LayoutDocument);
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

	// camelCase names, nulls omitted; enums carry their own string converters.
	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	[JsonSerializable(typeof(LayoutDocument))]
	private sealed partial class LayoutDiskJson : JsonSerializerContext;

	// Single-line for the host↔web bridge, where newlines would have to be escaped.
	[JsonSourceGenerationOptions(
		PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	[JsonSerializable(typeof(LayoutDocument))]
	private sealed partial class LayoutWireJson : JsonSerializerContext;
}
