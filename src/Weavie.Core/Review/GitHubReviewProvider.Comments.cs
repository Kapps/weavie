using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Weavie.Core.Review;

public sealed partial class GitHubReviewProvider {
	private readonly ConcurrentDictionary<string, ForgeUser> _viewers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, CommentPage> _commentPages = new(StringComparer.Ordinal);

	/// <inheritdoc/>
	public async Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		var result = new List<ReviewComment>();
		string? url = ApiBase(repo.Host) + $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments?per_page=100";
		// Each page is re-asked conditionally: GitHub answers an unchanged page 304, which costs no rate limit.
		while (url is not null) {
			using var request = await BuildRequestAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
			_commentPages.TryGetValue(url, out var cached);
			if (cached is not null) {
				request.Headers.TryAddWithoutValidation("If-None-Match", cached.EntityTag);
			}

			using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
			CommentPage page;
			if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) {
				page = cached;
			} else {
				string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
				ThrowForFailure(response, repo, body);
				page = new CommentPage(response.Headers.ETag?.ToString() ?? string.Empty, ParseComments(body), NextPage(response, repo));
				if (page.EntityTag.Length > 0) {
					_commentPages[url] = page;
				}
			}

			result.AddRange(page.Comments);
			url = page.Next;
		}

		// A comment posted mid-walk shifts the pages, so one can arrive twice.
		return [.. result.DistinctBy(c => c.Id)];
	}

	/// <inheritdoc/>
	public async Task<ReviewComment> AddAsync(RepoRef repo, int number, string commitId, NewReviewComment draft, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		ArgumentNullException.ThrowIfNull(draft);
		string payload = JsonSerializer.Serialize(new {
			body = draft.Body,
			commit_id = commitId,
			path = draft.Path,
			line = draft.Line,
			side = "RIGHT",
		});
		string body = await SendAsync(
			repo, HttpMethod.Post, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments", payload, ct).ConfigureAwait(false);
		return ParseComment(JsonDocument.Parse(body).RootElement);
	}

	/// <inheritdoc/>
	public async Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string body, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string payload = JsonSerializer.Serialize(new { body });
		string response = await SendAsync(
			repo, HttpMethod.Post, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments/{inReplyTo}/replies", payload, ct).ConfigureAwait(false);
		return ParseComment(JsonDocument.Parse(response).RootElement);
	}

	/// <inheritdoc/>
	public async Task<ReviewComment> EditAsync(RepoRef repo, long id, string body, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string payload = JsonSerializer.Serialize(new { body });
		string response = await SendAsync(
			repo, HttpMethod.Patch, $"/repos/{repo.Owner}/{repo.Name}/pulls/comments/{id}", payload, ct).ConfigureAwait(false);
		return ParseComment(JsonDocument.Parse(response).RootElement);
	}

	/// <inheritdoc/>
	public async Task<ForgeUser> ViewerAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		// Keyed by credential too, so switching accounts re-asks who "mine" is.
		string token = await ResolveTokenAsync(ct).ConfigureAwait(false);
		string key = $"{repo.Host}\n{token}";
		if (_viewers.TryGetValue(key, out var cached)) {
			return cached;
		}

		using var request = BuildRequest(HttpMethod.Get, ApiBase(repo.Host) + "/user", null, token);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		ThrowForFailure(response, repo, body);
		using var doc = JsonDocument.Parse(body);
		string login = String(doc.RootElement, "login");
		return login.Length > 0
			? _viewers.GetOrAdd(key, new ForgeUser(login, String(doc.RootElement, "avatar_url")))
			: throw new InvalidOperationException("GitHub didn't report the signed-in user's login.");
	}

	// The token rides every page request, so only follow a next link that stays on this repo's API host.
	private static string? NextPage(HttpResponseMessage response, RepoRef repo) {
		if (!response.Headers.TryGetValues("Link", out var links)) {
			return null;
		}

		foreach (string link in links.SelectMany(value => value.Split(','))) {
			string[] parts = link.Split(';', StringSplitOptions.TrimEntries);
			if (parts.Length > 1 && parts[1..].Contains("rel=\"next\"", StringComparer.Ordinal)) {
				string url = parts[0].Trim('<', '>');
				return url.StartsWith(ApiBase(repo.Host) + "/", StringComparison.OrdinalIgnoreCase)
					? url
					: throw new InvalidOperationException($"GitHub returned a next page on another host: {url}");
			}
		}

		return null;
	}

	/// <summary>Parses the GitHub <c>GET /pulls/{n}/comments</c> array into review comments. Pure, for tests.</summary>
	public static IReadOnlyList<ReviewComment> ParseComments(string json) {
		ArgumentNullException.ThrowIfNull(json);
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.ValueKind == JsonValueKind.Array
			? [.. doc.RootElement.EnumerateArray().Select(ParseComment)]
			: [];
	}

	// `line` is null once the anchor no longer exists in the PR's current diff: that thread is outdated.
	private static ReviewComment ParseComment(JsonElement c) => new() {
		Id = Long(c, "id"),
		Path = String(c, "path"),
		Line = Int(c, "line"),
		Outdated = !c.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number,
		Side = String(c, "side").Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? "left" : "right",
		Author = UserField(c, "login"),
		AuthorAvatarUrl = UserField(c, "avatar_url"),
		Url = String(c, "html_url"),
		Body = String(c, "body"),
		CreatedAt = String(c, "created_at"),
		UpdatedAt = String(c, "updated_at"),
		InReplyTo = Long(c, "in_reply_to_id"),
	};

	private sealed record CommentPage(string EntityTag, IReadOnlyList<ReviewComment> Comments, string? Next);

	private static string UserField(JsonElement comment, string name) =>
		comment.TryGetProperty("user", out var user) ? String(user, name) : string.Empty;
}
