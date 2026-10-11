using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.Core.Review;

// The PR overview in one GraphQL query; a connection longer than a page continues by node id, so nothing is cut off.
public sealed partial class GitHubReviewProvider : IPullRequestDiscussion {
	private const string ReviewStates = "states:[APPROVED,CHANGES_REQUESTED,COMMENTED]";
	private const string Actor = "author{login avatarUrl}";
	private const string CommentNode = "id body createdAt url " + Actor;
	private const string ReviewNode = "id body state submittedAt url " + Actor;
	private const string OpinionNode = "state " + Actor;
	private const string RequestNode = "requestedReviewer{__typename ... on User{login avatarUrl} ... on Bot{login avatarUrl}"
		+ " ... on Mannequin{login avatarUrl} ... on Team{slug avatarUrl}}";
	private const string CheckNode = "__typename ... on CheckRun{name status conclusion detailsUrl} ... on StatusContext{context state targetUrl}";

	private static readonly string DetailQuery =
		"query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){"
		+ "id number url title body isDraft createdAt " + Actor
		+ Page("comments", string.Empty, CommentNode)
		+ Page("reviews", ReviewStates, ReviewNode)
		+ Page("latestOpinionatedReviews", string.Empty, OpinionNode)
		+ Page("reviewRequests", string.Empty, RequestNode)
		+ " commits(last:1){nodes{commit{statusCheckRollup{id" + Page("contexts", string.Empty, CheckNode) + "}}}}}}}";

	/// <inheritdoc/>
	public async Task<PullRequestDetail> LoadDetailAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		var data = await GraphQlAsync(repo, DetailQuery, new JsonObject {
			["owner"] = repo.Owner,
			["name"] = repo.Name,
			["number"] = number,
		}, ct).ConfigureAwait(false);
		var pr = data.GetProperty("repository").GetProperty("pullRequest");
		if (pr.ValueKind != JsonValueKind.Object) {
			throw new InvalidOperationException($"GitHub has no PR #{number} in {repo.Owner}/{repo.Name}.");
		}

		string id = String(pr, "id");
		Task<IReadOnlyList<JsonElement>> All(string connection, string args, string fields) =>
			AllNodesAsync(repo, id, "PullRequest", connection, args, fields, pr.GetProperty(connection), ct);
		var rollup = pr.GetProperty("commits").GetProperty("nodes").EnumerateArray()
			.Select(n => n.GetProperty("commit").GetProperty("statusCheckRollup"))
			.FirstOrDefault(r => r.ValueKind == JsonValueKind.Object);
		var checks = rollup.ValueKind == JsonValueKind.Object
			? await AllNodesAsync(repo, String(rollup, "id"), "StatusCheckRollup", "contexts", string.Empty, CheckNode, rollup.GetProperty("contexts"), ct)
				.ConfigureAwait(false)
			: [];
		var reviews = await All("reviews", ReviewStates, ReviewNode).ConfigureAwait(false);
		return new PullRequestDetail(
			id,
			Int(pr, "number"),
			String(pr, "url"),
			String(pr, "title"),
			String(pr, "body"),
			Bool(pr, "isDraft"),
			Author(pr),
			String(pr, "createdAt"),
			Reviewers(
				Author(pr).Login,
				reviews,
				await All("latestOpinionatedReviews", string.Empty, OpinionNode).ConfigureAwait(false),
				await All("reviewRequests", string.Empty, RequestNode).ConfigureAwait(false)),
			[.. (await All("comments", string.Empty, CommentNode).ConfigureAwait(false))
				.Select(c => new PullRequestConversationItem(String(c, "id"), Author(c), String(c, "body"), null, String(c, "createdAt"), String(c, "url")))
				.Concat(reviews.Select(r => new PullRequestConversationItem(
					String(r, "id"), Author(r), String(r, "body"), ReviewerStateFor(String(r, "state")), String(r, "submittedAt"), String(r, "url"))))
				.OrderBy(item => item.CreatedAt, StringComparer.Ordinal)],
			[.. checks.Select(ParseCheck)]);
	}

	/// <inheritdoc/>
	public Task AddCommentAsync(RepoRef repo, string pullRequestId, string body, CancellationToken ct = default) =>
		MutateAsync(repo, "addComment", "AddCommentInput", "clientMutationId", new JsonObject {
			["subjectId"] = pullRequestId,
			["body"] = body,
		}, ct);

	private static string Page(string connection, string args, string fields) =>
		$" {connection}(first:100{(args.Length == 0 ? string.Empty : "," + args)}){{pageInfo{{hasNextPage endCursor}} nodes{{{fields}}}}}";

	private async Task<IReadOnlyList<JsonElement>> AllNodesAsync(
		RepoRef repo,
		string id,
		string type,
		string connection,
		string args,
		string fields,
		JsonElement page,
		CancellationToken ct) {
		var nodes = page.GetProperty("nodes").EnumerateArray().ToList();
		string query = $"query($id:ID!,$after:String){{node(id:$id){{... on {type}{{{Page(connection, args, fields).Replace("first:100", "first:100,after:$after", StringComparison.Ordinal)}}}}}}}";
		while (NextCursor(page) is { } after) {
			var data = await GraphQlAsync(repo, query, new JsonObject { ["id"] = id, ["after"] = after }, ct).ConfigureAwait(false);
			page = data.GetProperty("node").GetProperty(connection);
			nodes.AddRange(page.GetProperty("nodes").EnumerateArray());
		}

		return nodes;
	}

	/// <summary>Each reviewer's standing: a pending request, else their latest verdict, else that they commented. Pure, for tests.</summary>
	public static IReadOnlyList<PullRequestReviewer> Reviewers(
		string author,
		IReadOnlyList<JsonElement> reviews,
		IReadOnlyList<JsonElement> opinions,
		IReadOnlyList<JsonElement> requests) {
		var standing = new Dictionary<string, PullRequestReviewer>(StringComparer.OrdinalIgnoreCase);
		foreach (var review in reviews.Where(r => Author(r).Login != author)) {
			standing.TryAdd(Author(review).Login, new(Author(review), ReviewerState.Commented));
		}

		foreach (var opinion in opinions) {
			if (ReviewerStateFor(String(opinion, "state")) is { } state and not ReviewerState.Commented) {
				standing[Author(opinion).Login] = new(Author(opinion), state);
			}
		}

		foreach (var request in requests.Select(r => r.GetProperty("requestedReviewer")).Where(r => r.ValueKind == JsonValueKind.Object)) {
			var user = new ForgeUser(String(request, "__typename") == "Team" ? String(request, "slug") : String(request, "login"), String(request, "avatarUrl"));
			standing[user.Login] = new(user, ReviewerState.Pending);
		}

		return [.. standing.Values];
	}

	/// <summary>A check run or commit status as a check. Pure, for tests.</summary>
	public static PullRequestCheck ParseCheck(JsonElement context) =>
		String(context, "__typename") == "StatusContext"
			? new(String(context, "context"), String(context, "state") switch {
				"SUCCESS" => CheckState.Success,
				"FAILURE" or "ERROR" => CheckState.Failure,
				_ => CheckState.Pending,
			}, String(context, "targetUrl"))
			: new(String(context, "name"), String(context, "status") != "COMPLETED" ? CheckState.Pending : String(context, "conclusion") switch {
				"SUCCESS" => CheckState.Success,
				"NEUTRAL" or "SKIPPED" or "STALE" => CheckState.Neutral,
				_ => CheckState.Failure,
			}, String(context, "detailsUrl"));

	private static ReviewerState? ReviewerStateFor(string state) => state switch {
		"APPROVED" => ReviewerState.Approved,
		"CHANGES_REQUESTED" => ReviewerState.ChangesRequested,
		"COMMENTED" => ReviewerState.Commented,
		_ => null,
	};

	// A deleted account has no author; GitHub's own site shows it as "ghost".
	private static ForgeUser Author(JsonElement node) =>
		node.GetProperty("author") is { ValueKind: JsonValueKind.Object } author
			? new ForgeUser(String(author, "login"), String(author, "avatarUrl"))
			: new ForgeUser("ghost", string.Empty);
}
