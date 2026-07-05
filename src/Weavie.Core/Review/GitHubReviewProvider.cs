using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Review;

/// <summary>
/// The GitHub implementation of <see cref="IPullRequestProvider"/> and <see cref="IReviewCommentStore"/>: a thin
/// <see cref="HttpClient"/> client over the GitHub REST API, authenticated by an <see cref="IGitHubTokenSource"/>.
/// The host constructs one; presentation (the picker, the overview) and the diff (git) live elsewhere — this only
/// fetches/posts PR data.
/// </summary>
public sealed class GitHubReviewProvider : IPullRequestProvider, IReviewCommentStore {
	private const string DefaultHost = "github.com";
	private readonly HttpClient _http;
	private readonly IGitHubTokenSource _tokenSource;

	/// <summary>Creates the provider. Pass an <see cref="HttpClient"/> to share/mock; one is created otherwise.</summary>
	/// <param name="http">HTTP client for API calls; a default is created if null.</param>
	/// <param name="tokenSource">Resolves the API token.</param>
	public GitHubReviewProvider(HttpClient? http, IGitHubTokenSource tokenSource) {
		ArgumentNullException.ThrowIfNull(tokenSource);
		_http = http ?? new HttpClient();
		_tokenSource = tokenSource;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<PullRequestSummary>> ListOpenAsync(RepoRef repo, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string body = await SendAsync(
			repo, HttpMethod.Get, $"/repos/{repo.Owner}/{repo.Name}/pulls?state=open&sort=updated&direction=desc&per_page=50", null, ct).ConfigureAwait(false);
		return ParsePullRequests(body);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<PullRequestSummary>> SearchAsync(RepoRef repo, string query, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		// /search/issues ranks across the repo; scope to PRs. The result items are issue-shaped (no head/base
		// refs), so opening resolves them via GetAsync. The caller's query may add qualifiers (is:open, author:@me).
		string q = Uri.EscapeDataString($"repo:{repo.Owner}/{repo.Name} is:pr {query}".Trim());
		string body = await SendAsync(repo, HttpMethod.Get, $"/search/issues?q={q}&per_page=30", null, ct).ConfigureAwait(false);
		using var doc = JsonDocument.Parse(body);
		if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var result = new List<PullRequestSummary>();
		foreach (var item in items.EnumerateArray()) {
			result.Add(ParsePullRequest(item));
		}

		return result;
	}

	/// <inheritdoc/>
	public async Task<PullRequestSummary?> GetAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string? body = await SendOrNullAsync(repo, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}", ct).ConfigureAwait(false);
		return body is null ? null : ParsePullRequest(JsonDocument.Parse(body).RootElement);
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<ReviewComment>> ListAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string body = await SendAsync(
			repo, HttpMethod.Get, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments?per_page=100", null, ct).ConfigureAwait(false);
		return ParseComments(body);
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
			side = draft.Side.Equals("left", StringComparison.OrdinalIgnoreCase) ? "LEFT" : "RIGHT",
		});
		string body = await SendAsync(
			repo, HttpMethod.Post, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments", payload, ct).ConfigureAwait(false);
		return ParseComment(JsonDocument.Parse(body).RootElement);
	}

	/// <inheritdoc/>
	public async Task<ReviewComment> ReplyAsync(RepoRef repo, int number, long inReplyTo, string replyBody, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		string payload = JsonSerializer.Serialize(new { body = replyBody });
		string body = await SendAsync(
			repo, HttpMethod.Post, $"/repos/{repo.Owner}/{repo.Name}/pulls/{number}/comments/{inReplyTo}/replies", payload, ct).ConfigureAwait(false);
		return ParseComment(JsonDocument.Parse(body).RootElement);
	}

	// One authenticated GitHub REST call: resolves the token, attaches the standard headers, and returns the body
	// (throwing a clear message on a missing credential or a non-success status).
	private async Task<string> SendAsync(RepoRef repo, HttpMethod method, string path, string? jsonBody, CancellationToken ct) {
		using var request = await BuildRequestAsync(repo, method, path, jsonBody, ct).ConfigureAwait(false);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode} for {repo.Owner}/{repo.Name}.");
		}

		return body;
	}

	// A GET that returns null on 404 (the resource doesn't exist — e.g. a typed PR number that isn't there),
	// throwing on any other failure. Lets the caller distinguish "not found" from a real error.
	private async Task<string?> SendOrNullAsync(RepoRef repo, string path, CancellationToken ct) {
		using var request = await BuildRequestAsync(repo, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
			return null;
		}

		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			throw new InvalidOperationException($"GitHub API returned {(int)response.StatusCode} for {repo.Owner}/{repo.Name}.");
		}

		return body;
	}

	private async Task<HttpRequestMessage> BuildRequestAsync(RepoRef repo, HttpMethod method, string path, string? jsonBody, CancellationToken ct) {
		string? token = await _tokenSource.GetTokenAsync(ct).ConfigureAwait(false);
		if (string.IsNullOrEmpty(token)) {
			throw new InvalidOperationException("No GitHub credential found. Sign in to GitHub (gh auth login), or set GITHUB_TOKEN.");
		}

		var request = new HttpRequestMessage(method, ApiBase(repo.Host) + path);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Weavie", "1.0"));
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
		if (jsonBody is not null) {
			request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
		}

		return request;
	}

	/// <summary>The REST API base for a host: <c>api.github.com</c> for github.com, else Enterprise's <c>/api/v3</c>. Pure, for tests.</summary>
	public static string ApiBase(string host) =>
		string.Equals(host, DefaultHost, StringComparison.OrdinalIgnoreCase)
			? "https://api.github.com"
			: $"https://{host}/api/v3";

	/// <summary>
	/// The web-URL prefix a bare issue/PR number appends to — <c>https://{host}/{owner}/{repo}/pull/</c>. The web
	/// host equals the remote host on GitHub (public and Enterprise; only the API base differs), so this is right
	/// for both. Pure, for tests.
	/// </summary>
	public static string WebRefUrlBase(RepoRef repo) {
		ArgumentNullException.ThrowIfNull(repo);
		return $"https://{repo.Host}/{repo.Owner}/{repo.Name}/pull/";
	}

	/// <inheritdoc/>
	public string RefUrlBase(RepoRef repo) => WebRefUrlBase(repo);

	/// <summary>Parses the GitHub <c>GET /pulls</c> array into summaries. Pure, so it's testable without the network.</summary>
	public static IReadOnlyList<PullRequestSummary> ParsePullRequests(string json) {
		ArgumentNullException.ThrowIfNull(json);
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var result = new List<PullRequestSummary>();
		foreach (var pr in doc.RootElement.EnumerateArray()) {
			if (pr.TryGetProperty("number", out var numberEl) && numberEl.ValueKind == JsonValueKind.Number) {
				result.Add(ParsePullRequest(pr));
			}
		}

		return result;
	}

	/// <summary>
	/// Parses one PR object — from <c>/pulls</c> or <c>/pulls/{n}</c> (with head/base refs) or a <c>/search/issues</c>
	/// item (without them, left empty). Pure, for tests.
	/// </summary>
	public static PullRequestSummary ParsePullRequest(JsonElement pr) => new() {
		Number = Int(pr, "number"),
		Title = String(pr, "title"),
		Author = pr.TryGetProperty("user", out var user) ? String(user, "login") : string.Empty,
		HeadRef = pr.TryGetProperty("head", out var head) ? String(head, "ref") : string.Empty,
		BaseRef = pr.TryGetProperty("base", out var bse) ? String(bse, "ref") : string.Empty,
		Url = String(pr, "html_url"),
		IsDraft = pr.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
	};

	/// <summary>Parses the GitHub <c>GET /pulls/{n}/comments</c> array into review comments. Pure, for tests.</summary>
	public static IReadOnlyList<ReviewComment> ParseComments(string json) {
		ArgumentNullException.ThrowIfNull(json);
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var result = new List<ReviewComment>();
		foreach (var comment in doc.RootElement.EnumerateArray()) {
			result.Add(ParseComment(comment));
		}

		return result;
	}

	private static ReviewComment ParseComment(JsonElement c) {
		// `line` is the current-diff line; `original_line` is the fallback when the comment is on an unchanged-in-
		// this-push line. Side defaults to the head (RIGHT) side.
		int line = Int(c, "line") is var l && l > 0 ? l : Int(c, "original_line");
		return new ReviewComment {
			Id = Long(c, "id"),
			Path = String(c, "path"),
			Line = line,
			Side = String(c, "side").Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? "left" : "right",
			Author = c.TryGetProperty("user", out var user) ? String(user, "login") : string.Empty,
			Body = String(c, "body"),
			CreatedAt = String(c, "created_at"),
			InReplyTo = Long(c, "in_reply_to_id"),
		};
	}

	private static string String(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static int Int(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

	private static long Long(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : 0;
}
