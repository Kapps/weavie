using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.Core.Review;

// Review threads and pending reviews exist only in GitHub's GraphQL API: REST hides pending drafts and refuses
// replies while one exists. Each read costs one point of the viewer's GraphQL rate limit.
public sealed partial class GitHubReviewProvider {
	private const string CommentFields = "id body state viewerDidAuthor createdAt updatedAt url author{login avatarUrl}";

	private const string LoadQuery =
		"query($owner:String!,$name:String!,$number:Int!,$after:String){viewer{login avatarUrl}"
		+ "repository(owner:$owner,name:$name){pullRequest(number:$number){id viewerDidAuthor"
		+ " reviews(states:[PENDING],first:5){nodes{id body viewerDidAuthor}}"
		+ " reviewThreads(first:100,after:$after){pageInfo{hasNextPage endCursor} nodes{id path line diffSide isOutdated"
		+ " comments(first:100){pageInfo{hasNextPage endCursor} nodes{" + CommentFields + "}}}}}}}";

	private const string MoreCommentsQuery =
		"query($id:ID!,$after:String){node(id:$id){... on PullRequestReviewThread{"
		+ "comments(first:100,after:$after){pageInfo{hasNextPage endCursor} nodes{" + CommentFields + "}}}}}";

	/// <inheritdoc/>
	public async Task<PullRequestReview> LoadAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		PullRequestReview? review = null;
		var threads = new List<ReviewThread>();
		string? after = null;
		do {
			var data = await GraphQlAsync(repo, LoadQuery, new JsonObject {
				["owner"] = repo.Owner,
				["name"] = repo.Name,
				["number"] = number,
				["after"] = after,
			}, ct).ConfigureAwait(false);
			var pr = data.GetProperty("repository").GetProperty("pullRequest");
			if (pr.ValueKind != JsonValueKind.Object) {
				throw new InvalidOperationException($"GitHub has no PR #{number} in {repo.Owner}/{repo.Name}.");
			}

			review ??= ParseReview(data.GetProperty("viewer"), pr);
			var page = pr.GetProperty("reviewThreads");
			foreach (var thread in page.GetProperty("nodes").EnumerateArray()) {
				threads.Add(ParseThread(thread, await CommentsAsync(repo, thread, ct).ConfigureAwait(false)));
			}

			after = NextCursor(page);
		} while (after is not null);

		return review with { Threads = threads };
	}

	// A thread's first 100 comments ride the thread query; a longer one pages the rest by thread id.
	private async Task<IReadOnlyList<ReviewComment>> CommentsAsync(RepoRef repo, JsonElement thread, CancellationToken ct) {
		var page = thread.GetProperty("comments");
		var comments = page.GetProperty("nodes").EnumerateArray().Select(ParseComment).ToList();
		while (NextCursor(page) is { } after) {
			var data = await GraphQlAsync(repo, MoreCommentsQuery, new JsonObject {
				["id"] = String(thread, "id"),
				["after"] = after,
			}, ct).ConfigureAwait(false);
			page = data.GetProperty("node").GetProperty("comments");
			comments.AddRange(page.GetProperty("nodes").EnumerateArray().Select(ParseComment));
		}

		return comments;
	}

	/// <summary>Runs one GraphQL operation and returns its <c>data</c>; GitHub's <c>errors</c> become the exception's message.</summary>
	private async Task<JsonElement> GraphQlAsync(RepoRef repo, string query, JsonObject variables, CancellationToken ct) {
		string payload = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();
		using var request = await BuildRequestAsync(HttpMethod.Post, GraphQlUrl(repo.Host), payload, ct).ConfigureAwait(false);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		ThrowForFailure(response, repo, body);
		using var doc = JsonDocument.Parse(body);
		if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0) {
			string messages = string.Join(" — ", errors.EnumerateArray().Select(e => String(e, "message")).Where(m => m.Length > 0));
			throw new InvalidOperationException(messages.Length > 0
				? $"GitHub refused the request: {messages}"
				: $"GitHub refused the request ({String(errors[0], "type")}) without saying why.");
		}

		return doc.RootElement.GetProperty("data").Clone();
	}

	/// <summary>The GraphQL endpoint for a host: <c>api.github.com/graphql</c> for github.com, else Enterprise's <c>/api/graphql</c>. Pure, for tests.</summary>
	public static string GraphQlUrl(string host) =>
		string.Equals(host, DefaultHost, StringComparison.OrdinalIgnoreCase)
			? "https://api.github.com/graphql"
			: $"https://{host}/api/graphql";

	private static PullRequestReview ParseReview(JsonElement viewer, JsonElement pr) {
		var pending = pr.GetProperty("reviews").GetProperty("nodes").EnumerateArray()
			.Where(r => Bool(r, "viewerDidAuthor"))
			.Select(r => new PendingReview(String(r, "id"), String(r, "body")))
			.FirstOrDefault();
		return new PullRequestReview(
			String(pr, "id"),
			new ForgeUser(String(viewer, "login"), String(viewer, "avatarUrl")),
			Bool(pr, "viewerDidAuthor"),
			pending,
			[]);
	}

	// `line` is null once the anchor no longer exists in the PR's current diff: that thread is outdated.
	private static ReviewThread ParseThread(JsonElement thread, IReadOnlyList<ReviewComment> comments) {
		bool outdated = Bool(thread, "isOutdated") || thread.GetProperty("line").ValueKind != JsonValueKind.Number;
		return new ReviewThread(
			String(thread, "id"),
			String(thread, "path"),
			outdated ? 0 : Int(thread, "line"),
			String(thread, "diffSide").Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? "left" : "right",
			outdated,
			comments);
	}

	private static ReviewComment ParseComment(JsonElement c) {
		var author = c.GetProperty("author");
		// A deleted account's comments have no author; GitHub's own site shows them as "ghost".
		bool ghost = author.ValueKind != JsonValueKind.Object;
		return new ReviewComment {
			Id = String(c, "id"),
			Author = ghost ? "ghost" : String(author, "login"),
			AuthorAvatarUrl = ghost ? string.Empty : String(author, "avatarUrl"),
			Url = String(c, "url"),
			Body = String(c, "body"),
			CreatedAt = String(c, "createdAt"),
			UpdatedAt = String(c, "updatedAt"),
			Pending = String(c, "state") == "PENDING",
			Mine = Bool(c, "viewerDidAuthor"),
		};
	}

	private static string? NextCursor(JsonElement connection) {
		var info = connection.GetProperty("pageInfo");
		return Bool(info, "hasNextPage") ? String(info, "endCursor") : null;
	}

	private static bool Bool(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
