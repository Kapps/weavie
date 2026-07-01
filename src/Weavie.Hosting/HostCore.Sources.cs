using System.Text.Json;
using Weavie.Core.Sources;

namespace Weavie.Hosting;

// The source system's host wiring: connect a source (open its token page + ask the page for the user's pasted
// token, then validate + save it) and fetch a target into a read-only editor tab. The token files + fetch live in
// Core (SourceConnector); this only bridges them to the page. The full shadow-root SourceView, tab-kind routing,
// and web tabs are later phases (see docs/specs/web-and-source-tabs.md).
public sealed partial class HostCore {
	// A source URL the user opened before connecting: stashed when the open resolver routes to connect, then opened
	// once SaveSourceTokenAsync validates the token — so connecting from a click lands on the page they asked for.
	private string? _pendingSourceTarget;

	/// <summary>
	/// The open resolver: the page hands every opened URL here, and the host — which owns the sources and their
	/// <see cref="ISource.Match"/> — decides. A claimed URL is fetched + rendered natively when its source is
	/// connected, else it routes to the connect prompt (remembered, so it opens once connected); anything else is
	/// sent back as <c>open-web</c> for a web (iframe) tab. Keeping the match host-side means the web never
	/// re-implements a source's predicate.
	/// </summary>
	private void OpenTargetForWeb(string url) {
		if (!IsHttpUrl(url)) {
			return;
		}

		if (!_sources.Matches(url)) {
			_bridge.PostToWeb($"{{\"type\":\"open-web\",\"url\":{JsonString(url)}}}");
		} else if (_sources.IsConnected(url)) {
			_ = FetchSourceForWebAsync(url);
		} else {
			_pendingSourceTarget = url;
			PromptConnectNotion();
		}
	}

	/// <summary>
	/// Starts connecting Notion: opens Notion's token page in the browser and asks the page to show the token
	/// input. The user pastes their personal access token there; <see cref="SaveSourceTokenAsync"/> (driven by the
	/// <c>set-source-token</c> message) then validates and saves it. No file editing required.
	/// </summary>
	private void PromptConnectNotion() {
		_ui.Post(() => _platform.OpenExternalUrl(_sources.SetupUrlFor(NotionSource.SourceId)));
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "prompt-source-token",
			sourceId = NotionSource.SourceId,
			label = "Notion",
		}));
	}

	/// <summary>
	/// Validates and saves the access token the user pasted (the <c>set-source-token</c> message), toasts the
	/// connected workspace, and replies <c>source-token-result</c> (tagged by <paramref name="id"/>): on success
	/// the dialog closes, on a rejected/failed token it shows the reason inline so the user can fix it in place. A
	/// rejected token is never saved.
	/// </summary>
	private async Task SaveSourceTokenAsync(string id, string sourceId, string token) {
		if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(token)) {
			PostTokenResult(id, ok: false, "Enter a token to connect.");
			return;
		}

		try {
			string workspace = await _sources.SaveTokenAsync(sourceId, token, CancellationToken.None).ConfigureAwait(false);
			string where = string.IsNullOrWhiteSpace(workspace) ? "your Notion workspace" : $"Notion workspace “{workspace}”";
			Notify("info", $"Connected to {where}.");
			PostTokenResult(id, ok: true, string.Empty);
			// A URL opened before connecting (the resolver stashed it): now that we're connected, open it.
			if (Interlocked.Exchange(ref _pendingSourceTarget, null) is { } pending) {
				_ = FetchSourceForWebAsync(pending);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException) {
			// Surface inline in the still-open dialog (not a toast), so the user can correct the token without
			// restarting. TaskCanceledException covers HttpClient's own request timeout — without it a stalled Notion
			// endpoint would leave this fire-and-forget task faulted, the result never sent, and the dialog stuck on "Connecting…".
			PostTokenResult(id, ok: false, ex.Message);
		}
	}

	private void PostTokenResult(string id, bool ok, string error) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "source-token-result", id, ok, error }));

	/// <summary>
	/// Fetches a source <paramref name="target"/> (the matching source must be connected) and posts the host→web
	/// <c>source-doc</c> (keyed by <paramref name="target"/>) carrying the <c>markdown</c> the SourceView renders to
	/// HTML (and Claude reads). <c>source-loading</c> is posted first so the tab opens with a spinner while the fetch
	/// runs; a fetch failure posts <c>source-error</c> into that same tab.
	/// </summary>
	private async Task FetchSourceForWebAsync(string target) {
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "source-loading",
			target,
			title = GuessSourceTitle(target),
		}));
		SourceDoc doc;
		try {
			doc = await _sources.FetchAsync(target, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			// The spinner is already up, so EVERY failure must resolve it — not just the http/cancel set: a non-JSON
			// 200 (proxy / captive-portal / incident HTML) throws JsonException deeper in, and this is fire-and-forget,
			// so anything uncaught would leave the tab spinning forever. Surfaced loudly in the tab, never swallowed.
			_bridge.PostToWeb(JsonSerializer.Serialize(new {
				type = "source-error",
				target,
				message = ex.Message,
			}));
			return;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "source-doc",
			target,
			title = doc.Title,
			markdown = doc.Markdown,
			editedTime = doc.EditedTime,
		}));
	}

	// A best-effort tab label from a source URL's slug, shown while the real title loads: the last path segment with
	// a trailing 32-hex id stripped and dashes spaced (…/Test-Page-38e5…0055 → "Test Page"); "Notion" when there's none.
	private static string GuessSourceTitle(string url) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) {
			return "Notion";
		}

		string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
		string slug = (segments.Length > 0 ? segments[^1] : string.Empty).Split('?', '#')[0];
		int dash = slug.LastIndexOf('-');
		string tail = dash >= 0 ? slug[(dash + 1)..] : string.Empty;
		string name = dash > 0 && tail.Length == 32 && tail.All(Uri.IsHexDigit) ? slug[..dash] : slug;
		name = name.Replace('-', ' ').Trim();
		return name.Length > 0 ? name : "Notion";
	}
}
