using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Sources;

/// <summary>
/// The Notion <see cref="ISource"/>: a thin <see cref="HttpClient"/> client over the Notion API, authenticated by
/// a user-supplied personal access token. <see cref="Match"/> claims <c>notion.so</c>/<c>notion.site</c>/<c>app.notion.com</c> URLs;
/// <see cref="ValidateAsync"/> checks the token via <c>GET /v1/users/me</c>; <see cref="FetchAsync"/> reads a page's
/// title (<c>GET /v1/pages/{id}</c>) and its content as (enhanced) markdown (<c>GET /v1/pages/{id}/markdown</c>) into a
/// <see cref="SourceDoc"/>. Mirrors <c>GitHubReviewProvider</c> (injectable client, static pure parsers).
/// </summary>
public sealed class NotionSource : ISource {
	/// <summary>The Notion source id — the credential key and routing tag.</summary>
	public const string SourceId = "notion";

	private const string ApiVersion = "2026-03-11";

	private readonly HttpClient _http;

	/// <summary>Creates the source over <paramref name="http"/> (shared/mocked for the API calls).</summary>
	public NotionSource(HttpClient http) {
		ArgumentNullException.ThrowIfNull(http);
		_http = http;
	}

	/// <inheritdoc/>
	public string Id => SourceId;

	/// <inheritdoc/>
	public string SetupUrl => "https://app.notion.com/developers/tokens";

	/// <inheritdoc/>
	public bool Match(string target) => ClaimsUrl(target);

	/// <summary>True when <paramref name="target"/> is a <c>notion.so</c>/<c>notion.site</c> or <c>app.notion.com</c> http(s)
	/// URL — the hosts that serve pages (the rest of <c>notion.com</c> is marketing/help). Static so the headless fake shares one rule.</summary>
	public static bool ClaimsUrl(string target) =>
		Uri.TryCreate(target, UriKind.Absolute, out var uri)
		&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
		&& (uri.Host.Equals("notion.so", StringComparison.OrdinalIgnoreCase)
			|| uri.Host.EndsWith(".notion.so", StringComparison.OrdinalIgnoreCase)
			|| uri.Host.EndsWith(".notion.site", StringComparison.OrdinalIgnoreCase)
			|| uri.Host.Equals("app.notion.com", StringComparison.OrdinalIgnoreCase));

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
		string pageJson = await GetAsync($"https://api.notion.com/v1/pages/{id}", accessToken, ct).ConfigureAwait(false);
		string body = await GetAsync($"https://api.notion.com/v1/pages/{id}/markdown", accessToken, ct).ConfigureAwait(false);
		// The markdown is both the rendered surface (web-side) and Claude's reading channel — one projection.
		var markdown = ParseMarkdown(body);
		return new SourceDoc(ParseTitle(pageJson), markdown.Markdown, ParseEditedTime(pageJson), markdown.Truncated, markdown.UnknownBlocks);
	}

	/// <inheritdoc/>
	public async Task<SourceDoc> UpdateAsync(string target, string accessToken, string oldStr, string newStr, CancellationToken ct = default) {
		ArgumentException.ThrowIfNullOrEmpty(target);
		ArgumentException.ThrowIfNullOrEmpty(accessToken);
		ArgumentException.ThrowIfNullOrEmpty(oldStr);
		ArgumentException.ThrowIfNullOrEmpty(newStr);
		string id = ExtractPageId(target);
		using var request = BuildRequest(HttpMethod.Patch, $"https://api.notion.com/v1/pages/{id}/markdown", accessToken);
		// Targeted exact-match ops ONLY — never replace_content (it would destroy <unknown/> blocks and truncated
		// tails) and never allow_deleting_content / replace_all_matches (their absence is the safety rail).
		request.Content = new StringContent(
			JsonSerializer.Serialize(new {
				type = "update_content",
				update_content = new { content_updates = new[] { new { old_str = oldStr, new_str = newStr } } },
			}),
			Encoding.UTF8,
			"application/json");
		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.BadRequest && ValidationErrorMessage(payload) is { } conflict) {
			throw new SourceConflictException(conflict);
		}

		if (!response.IsSuccessStatusCode) {
			string detail = ApiErrorMessage(payload) is { } m ? $": {m}" : ".";
			throw new InvalidOperationException($"Notion API returned {(int)response.StatusCode} updating the page{detail}");
		}

		// The PATCH response returns the full updated page markdown — refresh the doc from it (title/editedTime
		// come from the same page GET the fetch path uses). Past this point the edit IS applied, so a refresh
		// failure must say so — reporting it as a failed save would invite a retry that then conflicts.
		var markdown = ParseMarkdown(payload);
		string pageJson;
		try {
			pageJson = await GetAsync($"https://api.notion.com/v1/pages/{id}", accessToken, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
			throw new InvalidOperationException($"The edit was saved, but refreshing the page failed — re-open it to see the result. ({ex.Message})", ex);
		}

		return new SourceDoc(ParseTitle(pageJson), markdown.Markdown, ParseEditedTime(pageJson), markdown.Truncated, markdown.UnknownBlocks);
	}

	// Notion reports every 400 as code "validation_error"; only the op-level failures — an old_str that is not
	// found or matches more than once (the page changed since the fetch) — are stale-page conflicts, and both
	// name "old_str" in the message. Anything else (a malformed body, a non-page id, a synced page) is a request
	// error, not a conflict — a "re-fetch" offer would be a dead end. Message sniffing is best-effort: a
	// request-shape error whose message names an old_str field path would still read as stale. Returns the
	// conflict reason, or null.
	private static string? ValidationErrorMessage(string payload) {
		try {
			using var doc = JsonDocument.Parse(payload);
			if (SourceJson.String(doc.RootElement, "code") != "validation_error") {
				return null;
			}

			string message = SourceJson.String(doc.RootElement, "message");
			return message.Contains("old_str", StringComparison.Ordinal) ? message : null;
		} catch (JsonException) {
			return null;
		}
	}

	// The API error body's "message", so a failed update surfaces Notion's reason, not just a status code.
	private static string? ApiErrorMessage(string payload) {
		try {
			using var doc = JsonDocument.Parse(payload);
			string message = SourceJson.String(doc.RootElement, "message");
			return message.Length > 0 ? message : null;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Reads a page's <c>last_edited_time</c> (ISO 8601) from its <c>GET /v1/pages/{id}</c> JSON, or empty. Pure, for tests.</summary>
	public static string ParseEditedTime(string pageJson) {
		ArgumentNullException.ThrowIfNull(pageJson);
		using var doc = JsonDocument.Parse(pageJson);
		return SourceJson.String(doc.RootElement, "last_edited_time");
	}

	/// <summary>
	/// A parsed <c>GET /v1/pages/{id}/markdown</c> response: the page's markdown verbatim plus the loss flags
	/// (page truncated / unreadable blocks) the web renders as a banner.
	/// </summary>
	/// <param name="Markdown">The page's (enhanced) markdown, byte-for-byte as Notion returned it.</param>
	/// <param name="Truncated">True when Notion cut the page off at its per-page block limit.</param>
	/// <param name="UnknownBlocks">How many blocks Notion couldn't read (0 when whole).</param>
	public sealed record MarkdownBody(string Markdown, bool Truncated, int UnknownBlocks);

	/// <summary>
	/// Reads a <c>GET /v1/pages/{id}/markdown</c> response (<c>{ markdown, truncated, unknown_block_ids }</c>) into a
	/// <see cref="MarkdownBody"/>. The markdown stays verbatim — the loss flags travel beside it (the web shows a
	/// banner) so the write path can diff edits against exactly what Notion returned. Pure, for tests.
	/// </summary>
	public static MarkdownBody ParseMarkdown(string responseJson) {
		ArgumentNullException.ThrowIfNull(responseJson);
		using var doc = JsonDocument.Parse(responseJson);
		var root = doc.RootElement;
		string markdown = SourceJson.String(root, "markdown");
		bool truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
		int unknown = root.TryGetProperty("unknown_block_ids", out var u) && u.ValueKind == JsonValueKind.Array ? u.GetArrayLength() : 0;
		return new MarkdownBody(markdown, truncated, unknown);
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
