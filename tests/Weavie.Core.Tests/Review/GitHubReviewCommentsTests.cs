using System.Net;
using System.Text.Json;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The comment half of <see cref="GitHubReviewProvider"/>: parsing, pagination, edit, and the viewer.</summary>
public sealed class GitHubReviewCommentsTests {
	private static readonly RepoRef Repo = new("github.com", "Kapps", "weavie");

	[Fact]
	public void ParseComments_MapsFieldsAndFlagsNullLinesOutdated() {
		var comments = GitHubReviewProvider.ParseComments("""
		[
		  { "id": 5, "path": "src/a.ts", "line": 12, "original_line": 9, "side": "RIGHT", "user": { "login": "bob" },
		    "body": "why?", "created_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-03T00:00:00Z", "in_reply_to_id": null },
		  { "id": 6, "path": "src/a.ts", "line": null, "original_line": 4, "side": "LEFT", "user": { "login": "ann" },
		    "body": "reply", "created_at": "2026-01-02T00:00:00Z", "updated_at": "2026-01-02T00:00:00Z", "in_reply_to_id": 5 }
		]
		""");

		Assert.Equal(
			new ReviewComment {
				Id = 5,
				Path = "src/a.ts",
				Line = 12,
				Outdated = false,
				Side = "right",
				Author = "bob",
				Body = "why?",
				CreatedAt = "2026-01-01T00:00:00Z",
				UpdatedAt = "2026-01-03T00:00:00Z",
				InReplyTo = 0,
			},
			comments[0]);
		Assert.True(comments[1].Outdated);
		Assert.Equal(0, comments[1].Line);
		Assert.Equal("left", comments[1].Side);
		Assert.Equal(5, comments[1].InReplyTo);
	}

	[Fact]
	public void ParsePullRequest_ReadsTheHeadSha() {
		using var doc = JsonDocument.Parse("""{ "number": 7, "head": { "ref": "feature", "sha": "abc123" } }""");

		Assert.Equal("abc123", GitHubReviewProvider.ParsePullRequest(doc.RootElement).HeadSha);
	}

	[Fact]
	public async Task ListAsync_FollowsEveryNextPage() {
		const string next = "https://api.github.com/repositories/1/pulls/7/comments?per_page=100&page=2";
		var handler = new ScriptedHandler(
			new Reply(HttpStatusCode.OK, Comment(1), $"<{next}>; rel=\"next\", <{next}>; rel=\"last\""),
			new Reply(HttpStatusCode.OK, Comment(2), null));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var comments = await provider.ListAsync(Repo, 7);

		Assert.Equal([1L, 2L], comments.Select(c => c.Id));
		Assert.Equal(
			["https://api.github.com/repos/Kapps/weavie/pulls/7/comments?per_page=100", next],
			handler.Requests.Select(r => r.Uri));
	}

	[Fact]
	public async Task ListAsync_RefusesANextPageOffTheApiHost() {
		var handler = new ScriptedHandler(
			new Reply(HttpStatusCode.OK, Comment(1), "<https://evil.example/steal>; rel=\"next\""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListAsync(Repo, 7));
		Assert.Single(handler.Requests);
	}

	[Fact]
	public async Task EditAsync_PatchesTheCommentBody() {
		var handler = new ScriptedHandler(new Reply(HttpStatusCode.OK, Comment(9)[1..^1], null));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var edited = await provider.EditAsync(Repo, 9, "better");

		var request = Assert.Single(handler.Requests);
		Assert.Equal("PATCH", request.Method);
		Assert.Equal("https://api.github.com/repos/Kapps/weavie/pulls/comments/9", request.Uri);
		Assert.Equal("""{"body":"better"}""", request.Body);
		Assert.Equal(9, edited.Id);
	}

	[Fact]
	public async Task ViewerLoginAsync_AsksOnceAndCaches() {
		var handler = new ScriptedHandler(new Reply(HttpStatusCode.OK, """{ "login": "Kapps" }""", null));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		Assert.Equal("Kapps", await provider.ViewerLoginAsync(Repo));
		Assert.Equal("Kapps", await provider.ViewerLoginAsync(Repo));
		Assert.Equal("https://api.github.com/user", Assert.Single(handler.Requests).Uri);
	}

	[Fact]
	public async Task AddAsync_SurfacesTheForgeValidationMessage() {
		var handler = new ScriptedHandler(new Reply(
			HttpStatusCode.UnprocessableEntity,
			"""{ "message": "Validation Failed", "errors": ["line must be part of the diff"] }""",
			null));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AddAsync(
			Repo, 7, new string('a', 40), new NewReviewComment { Path = "a.ts", Line = 3, Body = "hi" }));

		Assert.Contains("422", error.Message, StringComparison.Ordinal);
		Assert.Contains("Validation Failed — line must be part of the diff", error.Message, StringComparison.Ordinal);
		Assert.Contains("\"side\":\"RIGHT\"", handler.Requests[0].Body, StringComparison.Ordinal);
	}

	private static string Comment(long id) =>
		$$"""[{ "id": {{id}}, "path": "a.ts", "line": 1, "side": "RIGHT", "user": { "login": "bob" }, "body": "b" }]""";

	private sealed record Reply(HttpStatusCode Status, string Body, string? Link);

	private sealed record Recorded(string Method, string Uri, string? Body);

	private sealed class TokenSource : IGitHubTokenSource {
		public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>("token");
	}

	private sealed class ScriptedHandler(params Reply[] replies) : HttpMessageHandler {
		private readonly Queue<Reply> _replies = new(replies);

		public List<Recorded> Requests { get; } = [];

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			Requests.Add(new Recorded(request.Method.Method, request.RequestUri?.AbsoluteUri ?? string.Empty, body));
			var reply = _replies.Dequeue();
			var response = new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body) };
			if (reply.Link is not null) {
				response.Headers.Add("Link", reply.Link);
			}

			return response;
		}
	}
}
