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
	[InlineData("https://app.notion.com/p/Test-Page-38e539cbda7b80009371d99b7e333055", "38e539cb-da7b-8000-9371-d99b7e333055")]
	public void ExtractPageId_HandlesUrlsSlugsAndBareIds(string target, string expected) =>
		Assert.Equal(expected, NotionSource.ExtractPageId(target));

	[Fact]
	public void ExtractPageId_ThrowsWhenNoIdPresent() =>
		Assert.Throws<InvalidOperationException>(() => NotionSource.ExtractPageId("https://www.notion.so/just-a-title"));

	[Theory]
	[InlineData("https://www.notion.so/My-Page-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", true)]
	[InlineData("https://acme.notion.site/Public-Page", true)]
	[InlineData("https://app.notion.com/p/Test-Page-38e539cbda7b80009371d99b7e333055", true)]
	[InlineData("https://www.notion.com/product", false)] // marketing host, not a page host — opens as a web tab
	[InlineData("https://github.com/owner/repo", false)]
	[InlineData("https://notnotion.com/page", false)]
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

	[Fact]
	public void ParseEditedTime_ReadsLastEditedTime() =>
		Assert.Equal("2026-06-30T06:15:48.000Z",
			NotionSource.ParseEditedTime("""{ "last_edited_time": "2026-06-30T06:15:48.000Z" }"""));

	[Fact]
	public void ParseEditedTime_EmptyWhenAbsent() =>
		Assert.Equal(string.Empty, NotionSource.ParseEditedTime("""{ "object": "page" }"""));

	[Theory]
	[InlineData("""{ "bot": { "workspace_name": "Acme" }, "name": "Weavie bot" }""", "Acme")]
	[InlineData("""{ "name": "Just a name" }""", "Just a name")]
	[InlineData("""{ "object": "user" }""", "")]
	public void ParseWorkspaceName_PrefersBotWorkspaceThenName(string json, string expected) =>
		Assert.Equal(expected, NotionSource.ParseWorkspaceName(json));

	[Fact]
	public void ParseMarkdown_ReturnsTheMarkdownBodyVerbatim() =>
		Assert.Equal(
			"# Title\n\nHello **world**",
			NotionSource.ParseMarkdown("""{ "markdown": "# Title\n\nHello **world**", "truncated": false, "unknown_block_ids": [] }"""));

	[Fact]
	public void ParseMarkdown_PrependsNoticeWhenTruncated() {
		string result = NotionSource.ParseMarkdown("""{ "markdown": "# Big page", "truncated": true, "unknown_block_ids": [] }""");
		Assert.StartsWith("> **Note:** This page is incomplete", result);
		Assert.Contains("per-page block limit", result);
		Assert.EndsWith("# Big page", result); // the body still follows the notice
	}

	[Fact]
	public void ParseMarkdown_PrependsNoticeWhenBlocksUnreadable() {
		string result = NotionSource.ParseMarkdown("""{ "markdown": "body", "truncated": false, "unknown_block_ids": ["a", "b"] }""");
		Assert.StartsWith("> **Note:** This page is incomplete", result);
		Assert.Contains("2 block(s) couldn't be read", result);
	}

	[Fact]
	public void ParseMarkdown_NoNoticeWhenWhole() =>
		Assert.Equal("body", NotionSource.ParseMarkdown("""{ "markdown": "body", "truncated": false, "unknown_block_ids": [] }"""));
}
