using System.Net;
using System.Text.Json;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The review half of <see cref="GitHubReviewProvider"/>: the GraphQL read, its paging, and the writes.</summary>
public sealed class GitHubReviewCommentsTests {
	private static readonly RepoRef Repo = new("github.com", "Kapps", "weavie");

	[Fact]
	public async Task LoadAsync_ReadsThreadsDraftsAndTheViewersPendingReview() {
		var handler = new ScriptedHandler(Ok(Page(
			"""
			{ "id": "PRRT_1", "path": "src/a.ts", "line": 12, "diffSide": "RIGHT", "isOutdated": false,
			  "comments": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
			    { "id": "PRRC_1", "body": "why?", "state": "SUBMITTED", "viewerDidAuthor": false, "createdAt": "2026-01-01T00:00:00Z",
			      "updatedAt": "2026-01-03T00:00:00Z", "url": "https://github.com/Kapps/weavie/pull/7#discussion_r1",
			      "author": { "login": "bob", "avatarUrl": "https://a/bob" } },
			    { "id": "PRRC_2", "body": "because", "state": "PENDING", "viewerDidAuthor": true, "createdAt": "2026-01-02T00:00:00Z",
			      "updatedAt": "2026-01-02T00:00:00Z", "url": "", "author": null } ] } },
			{ "id": "PRRT_2", "path": "src/b.ts", "line": null, "diffSide": "LEFT", "isOutdated": true,
			  "comments": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [] } }
			""",
			hasNextPage: false)));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var review = await provider.LoadAsync(Repo, 7);

		Assert.Equal("https://api.github.com/graphql", Assert.Single(handler.Requests).Uri);
		using var request = JsonDocument.Parse(handler.Requests[0].Body!);
		Assert.Equal(7, request.RootElement.GetProperty("variables").GetProperty("number").GetInt32());
		Assert.Equal("PR_7", review.PullRequestId);
		Assert.Equal(new ForgeUser("Kapps", "https://a/k"), review.Viewer);
		Assert.True(review.ViewerIsAuthor);
		Assert.Equal(new PendingReview("PRR_mine", "draft summary"), review.Pending);
		var thread = review.Threads[0];
		Assert.Equal(("PRRT_1", "src/a.ts", 12, "right", false), (thread.Id, thread.Path, thread.Line, thread.Side, thread.Outdated));
		Assert.Equal(
			new ReviewComment {
				Id = "PRRC_1",
				Author = "bob",
				AuthorAvatarUrl = "https://a/bob",
				Url = "https://github.com/Kapps/weavie/pull/7#discussion_r1",
				Body = "why?",
				CreatedAt = "2026-01-01T00:00:00Z",
				UpdatedAt = "2026-01-03T00:00:00Z",
				Pending = false,
				Mine = false,
			},
			thread.Comments[0]);
		Assert.Equal(("ghost", true, true), (thread.Comments[1].Author, thread.Comments[1].Pending, thread.Comments[1].Mine));
		Assert.Equal((0, "left", true), (review.Threads[1].Line, review.Threads[1].Side, review.Threads[1].Outdated));
	}

	[Fact]
	public async Task LoadAsync_FollowsTheThreadCursorAndPagesALongThreadsComments() {
		var handler = new ScriptedHandler(
			Ok(Page(Thread("PRRT_1", hasMoreComments: true), hasNextPage: true)),
			Ok("""{ "data": { "node": { "comments": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [""" + Comment("late") + "] } } } }"),
			Ok(Page(Thread("PRRT_2", hasMoreComments: false), hasNextPage: false)));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var review = await provider.LoadAsync(Repo, 7);

		Assert.Equal(["PRRT_1", "PRRT_2"], review.Threads.Select(t => t.Id));
		Assert.Equal(["PRRT_1-c", "late"], review.Threads[0].Comments.Select(c => c.Id));
		Assert.Equal("cursor-PRRT_1", Variables(handler, 1).GetProperty("after").GetString());
		Assert.Equal("cursor-page", Variables(handler, 2).GetProperty("after").GetString());
	}

	[Fact]
	public async Task GraphQlErrors_SurfaceGitHubsMessageEvenOnA200() {
		var handler = new ScriptedHandler(
			Ok("""{ "data": { "submitPullRequestReview": null }, "errors": [{ "type": "UNPROCESSABLE", "message": "Can not approve your own pull request" }] }"""),
			Ok("""{ "data": { "addPullRequestReview": null }, "errors": [{ "type": "UNPROCESSABLE", "message": "" }] }"""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var own = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SubmitReviewAsync(Repo, "PRR_1", ReviewVerdict.Approve, ""));
		var silent = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			provider.CreateReviewAsync(Repo, "PR_7", "abc", ReviewVerdict.Comment, "", []));

		Assert.Equal("GitHub refused the request: Can not approve your own pull request", own.Message);
		Assert.Equal("GitHub refused the request (UNPROCESSABLE) without saying why.", silent.Message);
	}

	[Fact]
	public async Task CreateReviewAsync_LeavesAReviewWithoutAVerdictPending() {
		var handler = new ScriptedHandler(
			Ok("""{ "data": { "addPullRequestReview": { "pullRequestReview": { "id": "PRR_new" } } } }"""),
			Ok("""{ "data": { "addPullRequestReview": { "pullRequestReview": { "id": "PRR_done" } } } }"""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());
		var draft = new NewReviewComment { Path = "a.ts", Line = 3, Body = "hi" };

		Assert.Equal("PRR_new", await provider.CreateReviewAsync(Repo, "PR_7", "abc", null, "", [draft]));
		Assert.Equal("PRR_done", await provider.CreateReviewAsync(Repo, "PR_7", "abc", ReviewVerdict.RequestChanges, "fix it", []));

		var pending = Variables(handler, 0).GetProperty("input");
		Assert.False(pending.TryGetProperty("event", out _));
		Assert.Equal("abc", pending.GetProperty("commitOID").GetString());
		Assert.Equal("""{"path":"a.ts","line":3,"side":"RIGHT","body":"hi"}""", pending.GetProperty("threads")[0].GetRawText());
		Assert.Equal("REQUEST_CHANGES", Variables(handler, 1).GetProperty("input").GetProperty("event").GetString());
	}

	[Fact]
	public async Task ReplyAsync_NamesThePendingReviewOnlyWhenQueued() {
		var handler = new ScriptedHandler(
			Ok("""{ "data": { "addPullRequestReviewThreadReply": { "comment": { "id": "c" } } } }"""),
			Ok("""{ "data": { "addPullRequestReviewThreadReply": { "comment": { "id": "c" } } } }"""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		await provider.ReplyAsync(Repo, "PRRT_1", "PRR_1", "queued");
		await provider.ReplyAsync(Repo, "PRRT_1", null, "now");

		Assert.Equal("PRR_1", Variables(handler, 0).GetProperty("input").GetProperty("pullRequestReviewId").GetString());
		Assert.Equal(JsonValueKind.Null, Variables(handler, 1).GetProperty("input").GetProperty("pullRequestReviewId").ValueKind);
	}

	[Fact]
	public void ParsePullRequest_ReadsTheHeadSha() {
		using var doc = JsonDocument.Parse("""{ "number": 7, "head": { "ref": "feature", "sha": "abc123" } }""");

		Assert.Equal("abc123", GitHubReviewProvider.ParsePullRequest(doc.RootElement).HeadSha);
	}

	[Fact]
	public void GraphQlUrl_UsesEnterprisesApiPath() =>
		Assert.Equal("https://git.corp.example/api/graphql", GitHubReviewProvider.GraphQlUrl("git.corp.example"));

	private static Reply Ok(string body) => new(HttpStatusCode.OK, body);

	private static JsonElement Variables(ScriptedHandler handler, int request) =>
		JsonDocument.Parse(handler.Requests[request].Body!).RootElement.GetProperty("variables").Clone();

	private static string Page(string threads, bool hasNextPage) => $$"""
		{ "data": { "viewer": { "login": "Kapps", "avatarUrl": "https://a/k" }, "repository": { "pullRequest": {
		  "id": "PR_7", "viewerDidAuthor": true,
		  "reviews": { "nodes": [
		    { "id": "PRR_other", "body": "", "viewerDidAuthor": false },
		    { "id": "PRR_mine", "body": "draft summary", "viewerDidAuthor": true } ] },
		  "reviewThreads": { "pageInfo": { "hasNextPage": {{(hasNextPage ? "true" : "false")}}, "endCursor": "cursor-page" },
		    "nodes": [{{threads}}] } } } } }
		""";

	private static string Thread(string id, bool hasMoreComments) => $$"""
		{ "id": "{{id}}", "path": "a.ts", "line": 1, "diffSide": "RIGHT", "isOutdated": false,
		  "comments": { "pageInfo": { "hasNextPage": {{(hasMoreComments ? "true" : "false")}}, "endCursor": "cursor-{{id}}" },
		    "nodes": [{{Comment(id + "-c")}}] } }
		""";

	private static string Comment(string id) => $$"""
		{ "id": "{{id}}", "body": "b", "state": "SUBMITTED", "viewerDidAuthor": false, "createdAt": "", "updatedAt": "", "url": "",
		  "author": { "login": "bob", "avatarUrl": "" } }
		""";

	private sealed record Reply(HttpStatusCode Status, string Body);

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
			return response;
		}
	}
}
