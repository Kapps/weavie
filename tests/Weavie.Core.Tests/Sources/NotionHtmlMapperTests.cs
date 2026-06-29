using Weavie.Core.Sources;
using Xunit;

namespace Weavie.Core.Tests.Sources;

/// <summary>
/// Pure tests for <see cref="NotionHtmlMapper"/> — block + annotation coverage, the escaping/sanitization contract
/// (untrusted Notion text/urls), nesting, and graceful degradation. No network.
/// </summary>
public sealed class NotionHtmlMapperTests {
	// Wraps block JSON fragments in the {"results":[...]} envelope the mapper expects.
	private static string Html(params string[] blocks) => NotionHtmlMapper.ToHtml($$"""{ "results": [ {{string.Join(",", blocks)}} ] }""");

	private static string Para(string runs) => $$"""{ "type": "paragraph", "paragraph": { "rich_text": [ {{runs}} ] } }""";

	private static string Run(string text, string annotations) =>
		$$"""{ "type": "text", "plain_text": {{System.Text.Json.JsonSerializer.Serialize(text)}}, "annotations": { {{annotations}} } }""";

	[Fact]
	public void Annotations_WrapInSemanticTags() {
		string html = Html(Para(Run("hi", "\"bold\": true, \"italic\": true, \"code\": true")));
		Assert.Contains("<strong>", html);
		Assert.Contains("<em>", html);
		Assert.Contains("<code>hi</code>", html);
	}

	[Fact]
	public void Link_SafeHrefBecomesAnchor() {
		string html = Html(Para("""{ "type": "text", "plain_text": "site", "href": "https://notion.so/x" }"""));
		Assert.Contains("""<a href="https://notion.so/x">site</a>""", html);
	}

	[Theory]
	[InlineData("javascript:alert(1)")]
	[InlineData("data:text/html,<script>1</script>")]
	[InlineData("vbscript:msgbox")]
	public void Link_UnsafeHrefIsDropped_TextKept(string href) {
		string html = Html(Para($$"""{ "type": "text", "plain_text": "x", "href": {{System.Text.Json.JsonSerializer.Serialize(href)}} }"""));
		Assert.DoesNotContain("<a ", html);
		Assert.Contains("x", html);
	}

	[Fact]
	public void Text_IsHtmlEscaped() {
		string html = Html(Para("""{ "type": "text", "plain_text": "<script>alert(\"x\")</script> & <b>" }"""));
		Assert.DoesNotContain("<script>", html);
		Assert.Contains("&lt;script&gt;", html);
		Assert.Contains("&amp;", html);
	}

	[Fact]
	public void Color_KnownBecomesClass_UnknownDropped() {
		Assert.Contains("""<span class="wv-bg-blue">""", Html(Para(Run("c", "\"color\": \"blue_background\""))));
		Assert.DoesNotContain("<span", Html(Para(Run("c", "\"color\": \"chartreuse\""))));
	}

	[Fact]
	public void Headings_MapToH1ToH3() {
		string html = Html(
			"""{ "type": "heading_1", "heading_1": { "rich_text": [ { "plain_text": "A" } ] } }""",
			"""{ "type": "heading_2", "heading_2": { "rich_text": [ { "plain_text": "B" } ] } }""",
			"""{ "type": "heading_3", "heading_3": { "rich_text": [ { "plain_text": "C" } ] } }""");
		Assert.Equal("<h1>A</h1><h2>B</h2><h3>C</h3>", html);
	}

	[Fact]
	public void ListItems_GroupIntoOneList() {
		string item = """{ "type": "bulleted_list_item", "bulleted_list_item": { "rich_text": [ { "plain_text": "x" } ] } }""";
		Assert.Equal("<ul><li>x</li><li>x</li></ul>", Html(item, item));
	}

	[Fact]
	public void Todo_RendersDisabledCheckbox() {
		string html = Html("""{ "type": "to_do", "to_do": { "checked": true, "rich_text": [ { "plain_text": "done" } ] } }""");
		Assert.Contains("""<ul class="wv-todos">""", html);
		Assert.Contains("""<input type="checkbox" disabled checked>done""", html);
	}

	[Fact]
	public void Code_EscapesAndTagsLanguage() {
		string html = Html("""{ "type": "code", "code": { "language": "python", "rich_text": [ { "plain_text": "print(\"<x>\")" } ] } }""");
		Assert.Contains("""<pre><code class="language-python">""", html);
		Assert.Contains("&lt;x&gt;", html);
	}

	[Fact]
	public void Code_NormalizesLanguageAlias() =>
		Assert.Contains("""class="language-cpp">""",
			Html("""{ "type": "code", "code": { "language": "C++", "rich_text": [ { "plain_text": "x" } ] } }"""));

	[Fact]
	public void Code_MermaidBecomesPendingPlaceholder() =>
		Assert.Equal("""<pre class="mermaid-pending">graph TD; A--&gt;B</pre>""",
			Html("""{ "type": "code", "code": { "language": "mermaid", "rich_text": [ { "plain_text": "graph TD; A-->B" } ] } }"""));

	[Fact]
	public void Callout_HasIconColorAndBody() {
		string html = Html("""
			{ "type": "callout", "callout": { "color": "yellow_background", "icon": { "type": "emoji", "emoji": "💡" }, "rich_text": [ { "plain_text": "note" } ] } }
			""");
		Assert.Contains("""class="wv-callout wv-bg-yellow""", html);
		Assert.Contains("💡", html);
		Assert.Contains("note", html);
	}

	[Fact]
	public void Image_External_RendersFigure_UnsafeOmitted() {
		Assert.Contains("""<figure><img src="https://img/x.png" alt="">""",
			Html("""{ "type": "image", "image": { "type": "external", "external": { "url": "https://img/x.png" } } }"""));
		Assert.Equal(string.Empty,
			Html("""{ "type": "image", "image": { "type": "external", "external": { "url": "javascript:1" } } }"""));
	}

	[Fact]
	public void Toggle_NestsChildrenInDetails() {
		string html = Html("""
			{ "type": "toggle", "toggle": { "rich_text": [ { "plain_text": "more" } ] },
			  "children": [ { "type": "paragraph", "paragraph": { "rich_text": [ { "plain_text": "inner" } ] } } ] }
			""");
		Assert.Equal("<details><summary>more</summary><p>inner</p></details>", html);
	}

	[Fact]
	public void NestedList_RendersSublist() {
		string html = Html("""
			{ "type": "bulleted_list_item", "bulleted_list_item": { "rich_text": [ { "plain_text": "top" } ] },
			  "children": [ { "type": "bulleted_list_item", "bulleted_list_item": { "rich_text": [ { "plain_text": "sub" } ] } } ] }
			""");
		Assert.Equal("<ul><li>top<ul><li>sub</li></ul></li></ul>", html);
	}

	[Fact]
	public void Table_RendersHeaderRow() {
		string html = Html("""
			{ "type": "table", "table": { "has_column_header": true },
			  "children": [
			    { "type": "table_row", "table_row": { "cells": [ [ { "plain_text": "H" } ] ] } },
			    { "type": "table_row", "table_row": { "cells": [ [ { "plain_text": "v" } ] ] } } ] }
			""");
		Assert.Equal("<table><tr><th>H</th></tr><tr><td>v</td></tr></table>", html);
	}

	[Fact]
	public void Columns_RenderFlexWrappers() {
		string html = Html("""
			{ "type": "column_list", "column_list": {}, "children": [
			  { "type": "column", "column": {}, "children": [ { "type": "paragraph", "paragraph": { "rich_text": [ { "plain_text": "L" } ] } } ] } ] }
			""");
		Assert.Equal("""<div class="wv-columns"><div class="wv-column"><p>L</p></div></div>""", html);
	}

	[Fact]
	public void UnknownBlock_DegradesToTextOrOmits() {
		Assert.Equal("<p>kept</p>", Html("""{ "type": "quiz", "quiz": { "rich_text": [ { "plain_text": "kept" } ] } }"""));
		Assert.Equal(string.Empty, Html("""{ "type": "unsupported", "unsupported": {} }"""));
	}

	[Fact]
	public void Divider_RendersHr() => Assert.Equal("<hr>", Html("""{ "type": "divider", "divider": {} }"""));
}
