using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the pure bits of <see cref="GitHubReviewProvider"/> — the PR JSON parser and API-base resolution.</summary>
public sealed class GitHubReviewProviderTests {
	[Fact]
	public void ParsePullRequests_MapsFields() {
		string json = """
		[
		  { "number": 89, "title": "Open PR spec", "user": { "login": "Kapps" },
		    "head": { "ref": "claude/open-pr" }, "html_url": "https://github.com/Kapps/weavie/pull/89", "draft": false },
		  { "number": 74, "title": "readme", "user": { "login": "octocat" },
		    "head": { "ref": "feat/readme" }, "html_url": "https://github.com/Kapps/weavie/pull/74", "draft": true }
		]
		""";

		var prs = GitHubReviewProvider.ParsePullRequests(json);

		Assert.Equal(2, prs.Count);
		Assert.Equal(89, prs[0].Number);
		Assert.Equal("Open PR spec", prs[0].Title);
		Assert.Equal("Kapps", prs[0].Author);
		Assert.Equal("claude/open-pr", prs[0].HeadRef);
		Assert.Equal("https://github.com/Kapps/weavie/pull/89", prs[0].Url);
		Assert.False(prs[0].IsDraft);
		Assert.True(prs[1].IsDraft);
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("{}")]
	public void ParsePullRequests_EmptyForNoArrayOrEmpty(string json) =>
		Assert.Empty(GitHubReviewProvider.ParsePullRequests(json));

	[Fact]
	public void ParsePullRequest_SearchItem_HasNoRefs() {
		// A /search/issues item is issue-shaped: number/title/user but no head/base — refs resolve on open.
		using var doc = System.Text.Json.JsonDocument.Parse(
			"""{ "number": 12, "title": "fix", "user": { "login": "ann" }, "html_url": "u", "draft": true }""");

		var pr = GitHubReviewProvider.ParsePullRequest(doc.RootElement);

		Assert.Equal(12, pr.Number);
		Assert.Equal("ann", pr.Author);
		Assert.Equal(string.Empty, pr.HeadRef);
		Assert.Equal(string.Empty, pr.BaseRef);
		Assert.True(pr.IsDraft);
	}

	[Theory]
	[InlineData("github.com", "https://api.github.com")]
	[InlineData("github.example.com", "https://github.example.com/api/v3")]
	public void ApiBase_PicksPublicOrEnterprise(string host, string expected) =>
		Assert.Equal(expected, GitHubReviewProvider.ApiBase(host));

	[Theory]
	[InlineData("github.com", "owner", "repo", "https://github.com/owner/repo/pull/")]
	[InlineData("github.example.com", "org", "app", "https://github.example.com/org/app/pull/")]
	public void WebRefUrlBase_BuildsForgePullPrefixFromHost(string host, string owner, string name, string expected) =>
		Assert.Equal(expected, GitHubReviewProvider.WebRefUrlBase(new RepoRef(host, owner, name)));

	[Fact]
	public void ParseComments_MapsFieldsAndSideAndReply() {
		string json = """
		[
		  { "id": 5, "path": "src/a.ts", "line": 12, "side": "RIGHT", "user": { "login": "bob" },
		    "body": "why?", "created_at": "2026-01-01T00:00:00Z", "in_reply_to_id": null },
		  { "id": 6, "path": "src/a.ts", "original_line": 4, "side": "LEFT", "user": { "login": "ann" },
		    "body": "reply", "created_at": "2026-01-02T00:00:00Z", "in_reply_to_id": 5 }
		]
		""";

		var comments = GitHubReviewProvider.ParseComments(json);

		Assert.Equal(2, comments.Count);
		Assert.Equal(5, comments[0].Id);
		Assert.Equal("src/a.ts", comments[0].Path);
		Assert.Equal(12, comments[0].Line);
		Assert.Equal("right", comments[0].Side);
		Assert.Equal("bob", comments[0].Author);
		Assert.Equal(0, comments[0].InReplyTo);
		// `original_line` is the fallback when `line` is absent; LEFT → left side; reply carries its parent id.
		Assert.Equal(4, comments[1].Line);
		Assert.Equal("left", comments[1].Side);
		Assert.Equal(5, comments[1].InReplyTo);
	}
}
