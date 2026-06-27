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

	[Theory]
	[InlineData("github.com", "https://api.github.com")]
	[InlineData("github.example.com", "https://github.example.com/api/v3")]
	public void ApiBase_PicksPublicOrEnterprise(string host, string expected) =>
		Assert.Equal(expected, GitHubReviewProvider.ApiBase(host));
}
