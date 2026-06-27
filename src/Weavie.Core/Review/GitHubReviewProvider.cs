using System.Net.Http.Headers;
using System.Text.Json;

namespace Weavie.Core.Review;

/// <summary>
/// The GitHub implementation of <see cref="IPullRequestProvider"/>: a thin <see cref="HttpClient"/> client over
/// the GitHub REST API, authenticated by an <see cref="IGitHubTokenSource"/>. The host constructs one;
/// presentation (the overview source, the picker) and the diff (git) live elsewhere — this only fetches PR data.
/// </summary>
public sealed class GitHubReviewProvider : IPullRequestProvider {
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
		string? token = await _tokenSource.GetTokenAsync(ct).ConfigureAwait(false);
		if (string.IsNullOrEmpty(token)) {
			throw new InvalidOperationException(
				"No GitHub credential found. Sign in to GitHub (gh auth login), or set GITHUB_TOKEN.");
		}

		string url = $"{ApiBase(repo.Host)}/repos/{repo.Owner}/{repo.Name}/pulls?state=open&sort=updated&direction=desc&per_page=50";
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Weavie", "1.0"));
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			throw new InvalidOperationException(
				$"GitHub API returned {(int)response.StatusCode} for {repo.Owner}/{repo.Name}.");
		}

		return ParsePullRequests(body);
	}

	/// <summary>The REST API base for a host: <c>api.github.com</c> for github.com, else Enterprise's <c>/api/v3</c>. Pure, for tests.</summary>
	public static string ApiBase(string host) =>
		string.Equals(host, DefaultHost, StringComparison.OrdinalIgnoreCase)
			? "https://api.github.com"
			: $"https://{host}/api/v3";

	/// <summary>Parses the GitHub <c>GET /pulls</c> array into summaries. Pure, so it's testable without the network.</summary>
	public static IReadOnlyList<PullRequestSummary> ParsePullRequests(string json) {
		ArgumentNullException.ThrowIfNull(json);
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.ValueKind != JsonValueKind.Array) {
			return [];
		}

		var result = new List<PullRequestSummary>();
		foreach (var pr in doc.RootElement.EnumerateArray()) {
			if (!pr.TryGetProperty("number", out var numberEl) || numberEl.ValueKind != JsonValueKind.Number) {
				continue;
			}

			result.Add(new PullRequestSummary {
				Number = numberEl.GetInt32(),
				Title = String(pr, "title"),
				Author = pr.TryGetProperty("user", out var user) ? String(user, "login") : string.Empty,
				HeadRef = pr.TryGetProperty("head", out var head) ? String(head, "ref") : string.Empty,
				Url = String(pr, "html_url"),
				IsDraft = pr.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
			});
		}

		return result;
	}

	private static string String(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;
}
