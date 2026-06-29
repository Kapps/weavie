using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.Core.Sources;

/// <summary>
/// The Notion <see cref="ISource"/>: a thin <see cref="HttpClient"/> client over the Notion API, authenticated by
/// a user-supplied personal access token. <see cref="Match"/> claims <c>notion.so</c>/<c>notion.site</c> URLs;
/// <see cref="ValidateAsync"/> checks the token via <c>GET /v1/users/me</c>; <see cref="FetchAsync"/> reads a page
/// (title) and its child blocks (body) and maps them to a <see cref="SourceDoc"/>. Mirrors
/// <c>GitHubReviewProvider</c> (injectable client, static pure parsers).
/// </summary>
public sealed class NotionSource : ISource {
	/// <summary>The Notion source id — the credential key and routing tag.</summary>
	public const string SourceId = "notion";

	private const string ApiVersion = "2022-06-28";

	private readonly HttpClient _http;

	/// <summary>Creates the source over <paramref name="http"/> (shared/mocked for the API calls).</summary>
	public NotionSource(HttpClient http) {
		ArgumentNullException.ThrowIfNull(http);
		_http = http;
	}

	/// <inheritdoc/>
	public string Id => SourceId;

	/// <summary>The host patterns Notion claims — the one declaration that drives routing (host + web).</summary>
	public static IReadOnlyList<string> NotionHosts { get; } = ["notion.so", "*.notion.so", "*.notion.site"];

	/// <inheritdoc/>
	public string SetupUrl => "https://app.notion.com/developers/tokens";

	/// <inheritdoc/>
	public IReadOnlyList<string> Hosts => NotionHosts;

	/// <inheritdoc/>
	public bool Match(string target) => SourceHostMatch.Matches(Hosts, target);

	/// <inheritdoc/>
	public async Task<string> ValidateAsync(string accessToken, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(accessToken);
		using var request = BuildRequest(HttpMethod.Get, "https://api.notion.com/v1/users/me", accessToken);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) {
			throw new InvalidOperationException("Notion rejected the token — make sure it's a valid personal access token with Notion API access.");
		}

		string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			throw new InvalidOperationException($"Notion API returned {(int)response.StatusCode} validating the token.");
		}

		return ParseWorkspaceName(payload);
	}

	/// <inheritdoc/>
	public async Task<SourceDoc> FetchAsync(string target, string accessToken, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(target);
		ArgumentException.ThrowIfNullOrEmpty(accessToken);
		string id = ExtractPageId(target);
		string title = ParseTitle(await GetAsync($"https://api.notion.com/v1/pages/{id}", accessToken, ct).ConfigureAwait(false));
		string tree = await FetchBlockTreeAsync(id, accessToken, ct).ConfigureAwait(false);
		// One fetched tree → markdown (Claude's channel) + HTML (the rendered surface).
		return new SourceDoc(title, NotionBlockMapper.ToMarkdown(tree), NotionHtmlMapper.ToHtml(tree));
	}

	// The page's full block tree as a `{ "results": [...] }` JSON string, with each has_children block's descendants
	// attached as a `children` array, so the mappers walk one self-contained tree (no fetch callbacks).
	private async Task<string> FetchBlockTreeAsync(string blockId, string accessToken, CancellationToken ct) {
		var root = new JsonObject { ["results"] = await FetchChildrenAsync(blockId, accessToken, ct).ConfigureAwait(false) };
		return root.ToJsonString();
	}

	// All children of a block, paged in full (no cap — page until has_more is false), each has_children block
	// recursively populated. This is the nesting that makes toggles / columns / nested lists render.
	private async Task<JsonArray> FetchChildrenAsync(string blockId, string accessToken, CancellationToken ct) {
		var array = new JsonArray();
		string? cursor = null;
		do {
			string url = $"https://api.notion.com/v1/blocks/{blockId}/children?page_size=100"
				+ (cursor is null ? string.Empty : $"&start_cursor={Uri.EscapeDataString(cursor)}");
			using var doc = JsonDocument.Parse(await GetAsync(url, accessToken, ct).ConfigureAwait(false));
			var root = doc.RootElement;
			if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array) {
				foreach (var block in results.EnumerateArray()) {
					var node = JsonNode.Parse(block.GetRawText())!.AsObject();
					if (block.TryGetProperty("has_children", out var hc) && hc.ValueKind == JsonValueKind.True
						&& SourceJson.String(block, "id") is { Length: > 0 } childId) {
						node["children"] = await FetchChildrenAsync(childId, accessToken, ct).ConfigureAwait(false);
					}

					array.Add(node);
				}
			}

			cursor = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
				? SourceJson.String(root, "next_cursor") : null;
		} while (!string.IsNullOrEmpty(cursor));

		return array;
	}

	private async Task<string> GetAsync(string url, string accessToken, CancellationToken ct) {
		using var request = BuildRequest(HttpMethod.Get, url, accessToken);
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode) {
			throw new InvalidOperationException($"Notion API returned {(int)response.StatusCode} for {url}.");
		}

		return payload;
	}

	private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string accessToken) {
		var request = new HttpRequestMessage(method, url);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		request.Headers.Add("Notion-Version", ApiVersion);
		return request;
	}

	/// <summary>Reads the workspace name from a <c>GET /v1/users/me</c> response (<c>bot.workspace_name</c>, else <c>name</c>). Pure, for tests.</summary>
	public static string ParseWorkspaceName(string meJson) {
		ArgumentNullException.ThrowIfNull(meJson);
		using var doc = JsonDocument.Parse(meJson);
		var root = doc.RootElement;
		if (root.TryGetProperty("bot", out var bot) && SourceJson.String(bot, "workspace_name") is { Length: > 0 } workspace) {
			return workspace;
		}

		return SourceJson.String(root, "name");
	}

	/// <summary>
	/// Extracts the 32-hex page id from a Notion URL (the trailing run of hex on the last path segment, e.g.
	/// <c>…/Page-Title-1a2b…</c>) and formats it as a dashed UUID, or returns a bare id unchanged. Pure, for tests.
	/// </summary>
	public static string ExtractPageId(string target) {
		ArgumentException.ThrowIfNullOrEmpty(target);
		string segment = target;
		if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) {
			string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			segment = segments.Length > 0 ? segments[^1] : string.Empty;
		}

		segment = segment.Split('?', '#')[0];
		// A slug ends with the compact 32-hex id after the title (…-<id>); a bare/dashed id has no title prefix.
		string compact = (segment.Contains('-') ? segment[(segment.LastIndexOf('-') + 1)..] : segment).Replace("-", string.Empty);
		if (compact.Length != 32 || !compact.All(Uri.IsHexDigit)) {
			compact = segment.Replace("-", string.Empty); // a dashed UUID with no title prefix
		}

		if (compact.Length != 32 || !compact.All(Uri.IsHexDigit)) {
			throw new InvalidOperationException($"Couldn't find a Notion page id in '{target}'.");
		}

		return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..]}";
	}

	/// <summary>Reads a Notion page's title (the property whose type is <c>title</c>). Pure, for tests; empty when untitled.</summary>
	public static string ParseTitle(string pageJson) {
		ArgumentNullException.ThrowIfNull(pageJson);
		using var doc = JsonDocument.Parse(pageJson);
		if (!doc.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) {
			return string.Empty;
		}

		foreach (var property in properties.EnumerateObject()) {
			if (property.Value.TryGetProperty("type", out var type) && type.GetString() == "title"
				&& property.Value.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.Array) {
				var builder = new StringBuilder();
				foreach (var run in title.EnumerateArray()) {
					if (run.TryGetProperty("plain_text", out var p) && p.ValueKind == JsonValueKind.String) {
						builder.Append(p.GetString());
					}
				}

				return builder.ToString();
			}
		}

		return string.Empty;
	}
}
