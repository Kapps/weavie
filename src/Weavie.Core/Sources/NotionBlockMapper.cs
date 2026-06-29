using System.Text;
using System.Text.Json;

namespace Weavie.Core.Sources;

/// <summary>
/// Maps a Notion <c>GET /v1/blocks/{id}/children</c> response into a markdown projection. Pure and static, so the
/// block coverage is unit-tested without the network. Covers the common text blocks (paragraph, headings, lists,
/// to-dos, quote, code, divider); richer blocks (tables, columns, embeds) land with the SourceView phase.
/// </summary>
public static class NotionBlockMapper {
	/// <summary>Converts the children-list JSON into markdown text.</summary>
	public static string ToMarkdown(string childrenJson) {
		ArgumentNullException.ThrowIfNull(childrenJson);
		using var doc = JsonDocument.Parse(childrenJson);
		if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) {
			return string.Empty;
		}

		var lines = new List<string>();
		foreach (var block in results.EnumerateArray()) {
			lines.Add(MapBlock(block));
		}

		return string.Join("\n\n", lines.Where(line => line.Length > 0));
	}

	private static string MapBlock(JsonElement block) {
		string type = SourceJson.String(block, "type");
		if (type.Length == 0 || !block.TryGetProperty(type, out var content)) {
			return string.Empty;
		}

		return type switch {
			"paragraph" => RichText(content),
			"heading_1" => Prefixed("# ", content),
			"heading_2" => Prefixed("## ", content),
			"heading_3" => Prefixed("### ", content),
			"bulleted_list_item" => Prefixed("- ", content),
			"numbered_list_item" => Prefixed("1. ", content),
			"to_do" => Prefixed(Checked(content) ? "- [x] " : "- [ ] ", content),
			"quote" => Prefixed("> ", content),
			"callout" => Prefixed("> ", content),
			"code" => Code(content),
			"divider" => "---",
			_ => RichText(content),
		};
	}

	private static string Prefixed(string prefix, JsonElement content) {
		string text = RichText(content);
		return text.Length == 0 ? string.Empty : prefix + text;
	}

	private static string Code(JsonElement content) {
		string language = content.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? string.Empty : string.Empty;
		return $"```{language}\n{RichText(content)}\n```";
	}

	private static bool Checked(JsonElement content) =>
		content.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;

	/// <summary>Concatenates a block's <c>rich_text</c> run into plain text. Pure, for tests.</summary>
	public static string RichText(JsonElement content) {
		if (!content.TryGetProperty("rich_text", out var runs) || runs.ValueKind != JsonValueKind.Array) {
			return string.Empty;
		}

		var builder = new StringBuilder();
		foreach (var run in runs.EnumerateArray()) {
			if (run.TryGetProperty("plain_text", out var p) && p.ValueKind == JsonValueKind.String) {
				builder.Append(p.GetString());
			}
		}

		return builder.ToString();
	}
}
