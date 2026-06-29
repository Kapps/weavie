using System.Text;
using System.Text.Json;

namespace Weavie.Core.Sources;

/// <summary>
/// Maps a Notion block tree (a <c>/blocks/{id}/children</c> response whose <c>has_children</c> blocks have had
/// their descendants attached as a <c>children</c> array by <see cref="NotionSource"/>) into semantic HTML for the
/// shadow-root SourceView. Pure and static, unit-tested without the network; the per-block bodies live in the
/// <c>.Blocks.cs</c> partial. All API text is escaped via <see cref="HtmlEscape"/> / <see cref="NotionRichText"/>.
/// </summary>
public static partial class NotionHtmlMapper {
	/// <summary>Converts the (possibly nested) block-tree JSON into HTML.</summary>
	public static string ToHtml(string blocksJson) {
		ArgumentNullException.ThrowIfNull(blocksJson);
		using var doc = JsonDocument.Parse(blocksJson);
		var builder = new StringBuilder();
		if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array) {
			RenderBlocks(results, builder);
		}

		return builder.ToString();
	}

	// Renders a block array, grouping consecutive list items into one <ul>/<ol> (to-dos get their own group so
	// they never merge with bullets).
	private static void RenderBlocks(JsonElement blocks, StringBuilder builder) {
		string? openList = null;
		foreach (var block in blocks.EnumerateArray()) {
			string type = SourceJson.String(block, "type");
			string? list = ListGroup(type);
			if (list != openList) {
				CloseList(openList, builder);
				OpenList(list, builder);
				openList = list;
			}

			RenderBlock(block, type, builder);
		}

		CloseList(openList, builder);
	}

	private static string? ListGroup(string type) => type switch {
		"bulleted_list_item" => "ul",
		"numbered_list_item" => "ol",
		"to_do" => "todo",
		_ => null,
	};

	private static void OpenList(string? list, StringBuilder builder) {
		string? open = list switch {
			"ul" => "<ul>",
			"ol" => "<ol>",
			"todo" => "<ul class=\"wv-todos\">",
			_ => null,
		};
		if (open is not null) {
			builder.Append(open);
		}
	}

	private static void CloseList(string? list, StringBuilder builder) {
		if (list is "ul" or "todo") {
			builder.Append("</ul>");
		} else if (list == "ol") {
			builder.Append("</ol>");
		}
	}

	private static void RenderBlock(JsonElement block, string type, StringBuilder builder) {
		if (!block.TryGetProperty(type, out var content)) {
			RenderChildren(block, builder); // a block with no body of its own type — render any children, else nothing
			return;
		}

		string inline = NotionRichText.Render(content);
		switch (type) {
			case "paragraph":
				builder.Append($"<p>{inline}</p>");
				RenderChildren(block, builder);
				break;
			case "heading_1": builder.Append($"<h1>{inline}</h1>"); break;
			case "heading_2": builder.Append($"<h2>{inline}</h2>"); break;
			case "heading_3": builder.Append($"<h3>{inline}</h3>"); break;
			case "bulleted_list_item" or "numbered_list_item":
				builder.Append($"<li>{inline}");
				RenderChildren(block, builder);
				builder.Append("</li>");
				break;
			case "to_do":
				builder.Append($"<li class=\"wv-todo\"><input type=\"checkbox\" disabled{(Checked(content) ? " checked" : string.Empty)}>{inline}");
				RenderChildren(block, builder);
				builder.Append("</li>");
				break;
			case "quote":
				builder.Append($"<blockquote>{inline}");
				RenderChildren(block, builder);
				builder.Append("</blockquote>");
				break;
			case "divider": builder.Append("<hr>"); break;
			case "callout": AppendCallout(content, block, inline, builder); break;
			case "code": AppendCode(content, builder); break;
			case "image": AppendImage(content, builder); break;
			case "toggle": AppendToggle(block, inline, builder); break;
			case "table": AppendTable(block, builder); break;
			case "column_list": AppendColumns(block, builder); break;
			case "bookmark" or "embed" or "video" or "link_preview" or "file" or "pdf": AppendCard(content, builder); break;
			default:
				// Unknown block: degrade to its text if any, then its children; no error placeholder.
				if (inline.Length > 0) {
					builder.Append($"<p>{inline}</p>");
				}

				RenderChildren(block, builder);
				break;
		}
	}

	// Recurses into the children attached to a container block (nested lists, toggle/callout/column bodies).
	private static void RenderChildren(JsonElement block, StringBuilder builder) {
		if (block.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0) {
			RenderBlocks(children, builder);
		}
	}

	private static bool Checked(JsonElement content) =>
		content.TryGetProperty("checked", out var c) && c.ValueKind == JsonValueKind.True;
}
