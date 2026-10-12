using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The overview half of <see cref="GitHubReviewProvider"/>: one query, continued by node id, and the writes.</summary>
public sealed partial class GitHubReviewCommentsTests {
	[Fact]
	public async Task LoadDetailAsync_ReadsTheOverviewAndPagesALongConversation() {
		var handler = new ScriptedHandler(
			Ok(Detail(commentsHasNextPage: true)),
			Ok("""
				{ "data": { "node": { "comments": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
				  { "id": "IC_2", "body": "later", "createdAt": "2026-01-04T00:00:00Z", "url": "u2", "author": null } ] } } } }
				"""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		var detail = await provider.LoadDetailAsync(Repo, 7);

		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal("PR_7", Variables(handler, 1).GetProperty("id").GetString());
		Assert.Equal("cursor-comments", Variables(handler, 1).GetProperty("after").GetString());
		Assert.Equal(("PR_7", 7, "main", "feature", "Front door", "Adds it.", true),
			(detail.Id, detail.Number, detail.BaseRef, detail.HeadRef, detail.Title, detail.Body, detail.IsDraft));
		Assert.Equal(new ForgeUser("astra", "https://a/astra"), detail.Author);
		Assert.Equal(["IC_1", "PRR_0", "PRR_1", "IC_2"], detail.Conversation.Select(item => item.Id));
		Assert.Equal([null, ReviewerState.Commented, ReviewerState.ChangesRequested, null], detail.Conversation.Select(item => item.Review));
		Assert.Equal("ghost", detail.Conversation[3].Author.Login);
		Assert.Equal(
			[("kapps", ReviewerState.ChangesRequested), ("core-team", ReviewerState.Pending)],
			detail.Reviewers.Select(r => (r.User.Login, r.State)));
		Assert.Equal(
			[("e2e", CheckState.Failure), ("build", CheckState.Pending), ("lint", CheckState.Success), ("ci/legacy", CheckState.Failure)],
			detail.Checks.Select(c => (c.Name, c.State)));
	}

	[Fact]
	public async Task AddCommentAsync_PostsAPullRequestLevelComment() {
		var handler = new ScriptedHandler(Ok("""{ "data": { "addComment": { "clientMutationId": null } } }"""));
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		await provider.AddCommentAsync(Repo, "PR_7", "Looks good overall.");

		var input = Variables(handler, 0).GetProperty("input");
		Assert.Equal(("PR_7", "Looks good overall."), (input.GetProperty("subjectId").GetString(), input.GetProperty("body").GetString()));
	}

	private static string Detail(bool commentsHasNextPage) => $$"""
		{ "data": { "repository": { "pullRequest": {
		  "id": "PR_7", "number": 7, "url": "https://github.com/Kapps/weavie/pull/7", "baseRefName": "main",
		  "headRefName": "feature", "title": "Front door", "body": "Adds it.",
		  "isDraft": true, "createdAt": "2026-01-01T00:00:00Z", "author": { "login": "astra", "avatarUrl": "https://a/astra" },
		  "comments": { "pageInfo": { "hasNextPage": {{(commentsHasNextPage ? "true" : "false")}}, "endCursor": "cursor-comments" }, "nodes": [
		    { "id": "IC_1", "body": "first", "createdAt": "2026-01-02T00:00:00Z", "url": "u1", "author": { "login": "mira", "avatarUrl": "" } } ] },
		  "reviews": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
		    { "id": "PRR_1", "body": "", "state": "CHANGES_REQUESTED", "submittedAt": "2026-01-03T00:00:00Z", "url": "r1",
		      "author": { "login": "kapps", "avatarUrl": "" } },
		    { "id": "PRR_0", "body": "", "state": "COMMENTED", "submittedAt": "2026-01-02T12:00:00Z", "url": "r0",
		      "author": { "login": "astra", "avatarUrl": "" } } ] },
		  "latestOpinionatedReviews": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
		    { "state": "CHANGES_REQUESTED", "author": { "login": "kapps", "avatarUrl": "" } } ] },
		  "reviewRequests": { "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
		    { "requestedReviewer": { "__typename": "Team", "slug": "core-team", "avatarUrl": "" } } ] },
		  "commits": { "nodes": [ { "commit": { "statusCheckRollup": { "id": "SCR_1", "contexts": {
		    "pageInfo": { "hasNextPage": false, "endCursor": null }, "nodes": [
		      { "__typename": "CheckRun", "name": "e2e", "status": "COMPLETED", "conclusion": "TIMED_OUT", "detailsUrl": "d1" },
		      { "__typename": "CheckRun", "name": "build", "status": "IN_PROGRESS", "conclusion": null, "detailsUrl": "d2" },
		      { "__typename": "CheckRun", "name": "lint", "status": "COMPLETED", "conclusion": "SUCCESS", "detailsUrl": "d3" },
		      { "__typename": "StatusContext", "context": "ci/legacy", "state": "ERROR", "targetUrl": "d4" } ] } } } } ] }
		} } } }
		""";
}
