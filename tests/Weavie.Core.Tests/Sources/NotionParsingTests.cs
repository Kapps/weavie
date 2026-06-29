using Weavie.Core.Sources;
using Xunit;

namespace Weavie.Core.Tests.Sources;

/// <summary>
/// The pure Notion parsing surface — page-id extraction, title reading, and block→markdown mapping — exercised
/// without the network (the <c>GitHubReviewProvider.Parse*</c> testing model).
/// </summary>
public sealed class NotionParsingTests {
	[Theory]
	[InlineData("https://www.notion.so/My-Page-Title-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
	[InlineData("https://notion.so/1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
	[InlineData("1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
	[InlineData("1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
	[InlineData("https://www.notion.so/team/Page-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d?pvs=4", "1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d")]
	public void ExtractPageId_HandlesUrlsSlugsAndBareIds(string target, string expected) =>
		Assert.Equal(expected, NotionSource.ExtractPageId(target));

	[Fact]
	public void ExtractPageId_ThrowsWhenNoIdPresent() =>
		Assert.Throws<InvalidOperationException>(() => NotionSource.ExtractPageId("https://www.notion.so/just-a-title"));

	[Theory]
	[InlineData("https://www.notion.so/My-Page-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", true)]
	[InlineData("https://acme.notion.site/Public-Page", true)]
	[InlineData("https://github.com/owner/repo", false)]
	[InlineData("/local/path", false)]
	public void Match_ClaimsNotionHostsOnly(string target, bool expected) =>
		Assert.Equal(expected, new NotionSource(new HttpClient()).Match(target));

	[Fact]
	public void ParseTitle_ReadsTheTitleProperty() {
		const string json = """
		{ "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Roadmap " }, { "plain_text": "2026" } ] } } }
		""";
		Assert.Equal("Roadmap 2026", NotionSource.ParseTitle(json));
	}

	[Fact]
	public void ParseTitle_EmptyWhenNoTitleProperty() =>
		Assert.Equal(string.Empty, NotionSource.ParseTitle("""{ "properties": { "Status": { "type": "select" } } }"""));

	[Theory]
	[InlineData("""{ "bot": { "workspace_name": "Acme" }, "name": "Weavie bot" }""", "Acme")]
	[InlineData("""{ "name": "Just a name" }""", "Just a name")]
	[InlineData("""{ "object": "user" }""", "")]
	public void ParseWorkspaceName_PrefersBotWorkspaceThenName(string json, string expected) =>
		Assert.Equal(expected, NotionSource.ParseWorkspaceName(json));

	[Fact]
	public void ToMarkdown_MapsTheCommonBlocks() {
		const string json = """
		{ "results": [
			{ "type": "heading_1", "heading_1": { "rich_text": [ { "plain_text": "Title" } ] } },
			{ "type": "paragraph", "paragraph": { "rich_text": [ { "plain_text": "Hello world" } ] } },
			{ "type": "bulleted_list_item", "bulleted_list_item": { "rich_text": [ { "plain_text": "one" } ] } },
			{ "type": "to_do", "to_do": { "checked": true, "rich_text": [ { "plain_text": "done" } ] } },
			{ "type": "code", "code": { "language": "python", "rich_text": [ { "plain_text": "print(1)" } ] } },
			{ "type": "divider", "divider": {} }
		] }
		""";
		string markdown = NotionBlockMapper.ToMarkdown(json);
		Assert.Equal(
			"# Title\n\nHello world\n\n- one\n\n- [x] done\n\n```python\nprint(1)\n```\n\n---",
			markdown);
	}

	[Fact]
	public void ToMarkdown_SkipsEmptyBlocksAndHandlesNoResults() {
		Assert.Equal(string.Empty, NotionBlockMapper.ToMarkdown("""{ "object": "list" }"""));
		Assert.Equal("kept", NotionBlockMapper.ToMarkdown("""
		{ "results": [
			{ "type": "paragraph", "paragraph": { "rich_text": [] } },
			{ "type": "paragraph", "paragraph": { "rich_text": [ { "plain_text": "kept" } ] } }
		] }
		"""));
	}
}
