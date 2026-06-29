using System.Linq;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Sources;

// The per-block-type HTML emitters for NotionHtmlMapper, split out so neither file crosses ~300 lines. Every URL
// goes through HtmlEscape.SafeUrl (unsafe → dropped) and every text through HtmlEscape / NotionRichText.
public static partial class NotionHtmlMapper {
	private static void AppendCallout(JsonElement content, JsonElement block, string inline, StringBuilder builder) {
		string? colorClass = NotionRichText.ColorClass(SourceJson.String(content, "color"));
		string cls = colorClass is null ? "wv-callout" : $"wv-callout {colorClass}";
		builder.Append($"<aside class=\"{cls}\">{Icon(content)}<div class=\"wv-callout-body\">{inline}");
		RenderChildren(block, builder);
		builder.Append("</div></aside>");
	}

	private static void AppendCode(JsonElement content, StringBuilder builder) {
		string code = HtmlEscape.Text(NotionBlockMapper.RichText(content));
		string language = CodeLanguage(SourceJson.String(content, "language"));
		if (language == "mermaid") {
			// The SourceView's shared hydrateMermaid pass renders this placeholder's source to themed SVG after mount.
			builder.Append($"<pre class=\"mermaid-pending\">{code}</pre>");
			return;
		}

		// language-* lets the SourceView re-highlight with the shared hljs pass after mount; the text is pre-escaped.
		builder.Append($"<pre><code class=\"language-{language}\">{code}</code></pre>");
	}

	// Normalizes a Notion code language to the token our renderer uses (the shared highlight.js subset): the few
	// divergent names are aliased, the rest lowercased and stripped to letters/digits (so the class is clean and an
	// unregistered language just renders as plain escaped text). Empty for "plain text".
	private static string CodeLanguage(string language) => language.Trim().ToLowerInvariant() switch {
		"c++" => "cpp",
		"c#" => "csharp",
		"shell" or "sh" => "bash",
		"html" => "xml",
		"plain text" => string.Empty,
		var other => new string([.. other.Where(char.IsAsciiLetterOrDigit)]),
	};

	private static void AppendImage(JsonElement content, StringBuilder builder) {
		if (HtmlEscape.SafeUrl(MediaUrl(content)) is not { } src) {
			return; // missing/unsafe image url — omit the figure rather than emit a broken/dangerous <img>
		}

		string caption = Caption(content);
		string figcaption = caption.Length > 0 ? $"<figcaption>{caption}</figcaption>" : string.Empty;
		builder.Append($"<figure><img src=\"{src}\" alt=\"\">{figcaption}</figure>");
	}

	private static void AppendToggle(JsonElement block, string inline, StringBuilder builder) {
		builder.Append($"<details><summary>{inline}</summary>");
		RenderChildren(block, builder);
		builder.Append("</details>");
	}

	private static void AppendTable(JsonElement block, StringBuilder builder) {
		bool header = block.TryGetProperty("table", out var table)
			&& table.TryGetProperty("has_column_header", out var h) && h.ValueKind == JsonValueKind.True;
		builder.Append("<table>");
		if (block.TryGetProperty("children", out var rows) && rows.ValueKind == JsonValueKind.Array) {
			int index = 0;
			foreach (var row in rows.EnumerateArray()) {
				string cell = header && index == 0 ? "th" : "td";
				builder.Append("<tr>");
				if (row.TryGetProperty("table_row", out var tr) && tr.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array) {
					foreach (var runs in cells.EnumerateArray()) {
						builder.Append($"<{cell}>{NotionRichText.RenderRuns(runs)}</{cell}>");
					}
				}

				builder.Append("</tr>");
				index++;
			}
		}

		builder.Append("</table>");
	}

	private static void AppendColumns(JsonElement block, StringBuilder builder) {
		builder.Append("<div class=\"wv-columns\">");
		if (block.TryGetProperty("children", out var columns) && columns.ValueKind == JsonValueKind.Array) {
			foreach (var column in columns.EnumerateArray()) {
				builder.Append("<div class=\"wv-column\">");
				RenderChildren(column, builder);
				builder.Append("</div>");
			}
		}

		builder.Append("</div>");
	}

	private static void AppendCard(JsonElement content, StringBuilder builder) {
		string url = SourceJson.String(content, "url") is { Length: > 0 } u ? u : MediaUrl(content);
		if (HtmlEscape.SafeUrl(url) is not { } href) {
			return; // a card with no safe url is just dropped (the spec renders embeds as a link, never a live frame)
		}

		string caption = Caption(content);
		string label = caption.Length > 0 ? caption : HtmlEscape.Text(url);
		builder.Append($"<a class=\"wv-card\" href=\"{href}\">{label}</a>");
	}

	// The url of a file/external media block (image/video/file/pdf): content.<type>.url.
	private static string MediaUrl(JsonElement content) {
		string type = SourceJson.String(content, "type");
		return type.Length > 0 && content.TryGetProperty(type, out var file) ? SourceJson.String(file, "url") : string.Empty;
	}

	private static string Caption(JsonElement content) =>
		content.TryGetProperty("caption", out var caption) && caption.ValueKind == JsonValueKind.Array
			? NotionRichText.RenderRuns(caption) : string.Empty;

	// A block's icon (callout): an emoji span, or an escaped <img> for a file/external icon, or nothing.
	private static string Icon(JsonElement content) {
		if (!content.TryGetProperty("icon", out var icon) || icon.ValueKind != JsonValueKind.Object) {
			return string.Empty;
		}

		string type = SourceJson.String(icon, "type");
		if (type == "emoji") {
			return $"<span class=\"wv-icon\">{HtmlEscape.Text(SourceJson.String(icon, "emoji"))}</span>";
		}

		return icon.TryGetProperty(type, out var file) && HtmlEscape.SafeUrl(SourceJson.String(file, "url")) is { } src
			? $"<img class=\"wv-icon\" src=\"{src}\" alt=\"\">"
			: string.Empty;
	}
}
