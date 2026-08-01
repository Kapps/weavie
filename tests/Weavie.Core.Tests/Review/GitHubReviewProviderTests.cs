using System.Net;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Tests for the pure bits of <see cref="GitHubReviewProvider"/> — the PR JSON parser and API-base resolution.</summary>
public sealed class GitHubReviewProviderTests {
	[Fact]
	public async Task FindForBranchAsync_QueriesTheExactOpenHeadFirst() {
		var handler = new RecordingHandler("""[{"number":123,"title":"Native PR","html_url":"https://github.com/Kapps/weavie/pull/123","head":{"ref":"feat/native-ui-pr"}}]""");
		var provider = new GitHubReviewProvider(new HttpClient(handler), new StaticTokenSource());

		var result = await provider.FindForBranchAsync(
			new RepoRef("github.com", "Kapps", "weavie"), "contributor", "feat/native-ui-pr");

		Assert.Equal(123, result?.Number);
		string url = Assert.Single(handler.Requests).RequestUri?.AbsoluteUri ?? string.Empty;
		Assert.Contains("state=open", url, StringComparison.Ordinal);
		Assert.Contains("head=contributor%3Afeat%2Fnative-ui-pr", url, StringComparison.Ordinal);
		Assert.Contains("sort=updated&direction=desc&per_page=1", url, StringComparison.Ordinal);
	}

	[Fact]
	public async Task FindForBranchAsync_ReusesTheEtagWithTheCurrentCredential() {
		var handler = new ConditionalHandler();
		var tokens = new CountingTokenSource();
		var provider = new GitHubReviewProvider(new HttpClient(handler), tokens);
		var repo = new RepoRef("github.com", "Kapps", "weavie");

		var first = await provider.FindForBranchAsync(repo, "Kapps", "feature");
		var second = await provider.FindForBranchAsync(repo, "Kapps", "feature");

		Assert.Equal(123, first?.Number);
		Assert.Equal(first, second);
		Assert.Equal(2, handler.Calls);
		Assert.Equal("\"branch-v1\"", handler.SecondIfNoneMatch);
		Assert.Equal(["token-1", "token-2"], handler.Tokens);
		Assert.Equal(2, tokens.Calls);
	}

	[Fact]
	public async Task FindForBranchAsync_RequiresAuthentication() {
		var handler = new RecordingHandler("[]");
		var provider = new GitHubReviewProvider(new HttpClient(handler), new NullTokenSource());

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.FindForBranchAsync(
			new RepoRef("github.com", "Kapps", "weavie"), "Kapps", "feature"));

		Assert.Contains("No GitHub credential found", error.Message, StringComparison.Ordinal);
		Assert.Empty(handler.Requests);
	}

	[Fact]
	public async Task FindForBranchAsync_FallsBackToTheNewestFinalState() {
		var handler = new RecordingHandler(
			"[]",
			"""[{"number":122,"state":"closed","merged_at":"2026-08-01T00:00:00Z"}]""");
		var tokens = new CountingTokenSource();
		var provider = new GitHubReviewProvider(new HttpClient(handler), tokens);

		var result = await provider.FindForBranchAsync(
			new RepoRef("github.com", "Kapps", "weavie"), "Kapps", "feature");

		Assert.Equal(122, result?.Number);
		Assert.Equal(PullRequestState.Merged, result?.State);
		Assert.Equal(2, handler.Requests.Count);
		Assert.Contains("state=open", handler.Requests[0].RequestUri?.Query, StringComparison.Ordinal);
		Assert.Contains("state=closed", handler.Requests[1].RequestUri?.Query, StringComparison.Ordinal);
		Assert.Equal(1, tokens.Calls);

		using var mergedJson = System.Text.Json.JsonDocument.Parse(
			"""{"number":122,"state":"closed","merged_at":"2026-08-01T00:00:00Z"}""");
		using var closedJson = System.Text.Json.JsonDocument.Parse(
			"""{"number":121,"state":"closed","merged_at":null}""");
		Assert.Equal(PullRequestState.Merged, GitHubReviewProvider.ParsePullRequest(mergedJson.RootElement).State);
		Assert.Equal(PullRequestState.Closed, GitHubReviewProvider.ParsePullRequest(closedJson.RootElement).State);
	}

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
		Assert.Equal(PullRequestState.Open, prs[0].State);
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

	private sealed class StaticTokenSource : IGitHubTokenSource {
		public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>("token");
	}

	private sealed class CountingTokenSource : IGitHubTokenSource {
		public int Calls { get; private set; }

		public Task<string?> GetTokenAsync(CancellationToken ct = default) {
			Calls++;
			return Task.FromResult<string?>($"token-{Calls}");
		}
	}

	private sealed class NullTokenSource : IGitHubTokenSource {
		public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
	}

	private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler {
		private readonly Queue<string> _responses = new(responses);

		public List<HttpRequestMessage> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Requests.Add(request);
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
				Content = new StringContent(_responses.Dequeue()),
			});
		}
	}

	private sealed class ConditionalHandler : HttpMessageHandler {
		public int Calls { get; private set; }

		public string? SecondIfNoneMatch { get; private set; }
		public List<string?> Tokens { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) {
			Calls++;
			Tokens.Add(request.Headers.Authorization?.Parameter);
			if (Calls == 1) {
				var response = new HttpResponseMessage(HttpStatusCode.OK) {
					Content = new StringContent("""[{"number":123,"state":"open"}]"""),
				};
				response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"branch-v1\"");
				return Task.FromResult(response);
			}

			SecondIfNoneMatch = request.Headers.IfNoneMatch.Single().ToString();
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
		}
	}

}
