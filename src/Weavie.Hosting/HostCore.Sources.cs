using System.Text.Json;
using Weavie.Core.Sources;

namespace Weavie.Hosting;

// The source system's host wiring: connect a source (open its token page + ask the page for the user's pasted
// token, then validate + save it) and fetch a target into a read-only editor tab. The token files + fetch live in
// Core (SourceConnector); this only bridges them to the page. The full shadow-root SourceView, tab-kind routing,
// and web tabs are later phases (see docs/specs/web-and-source-tabs.md).
public sealed partial class HostCore {
	/// <summary>
	/// Starts connecting Notion: opens Notion's token page in the browser and asks the page to show the token
	/// input. The user pastes their personal access token there; <see cref="SaveSourceTokenAsync"/> (driven by the
	/// <c>set-source-token</c> message) then validates and saves it. No file editing required.
	/// </summary>
	private void PromptConnectNotion() {
		_ui.Post(() => _platform.OpenExternalUrl(_sources.SetupUrlFor(NotionSource.SourceId)));
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "prompt-source-token", sourceId = NotionSource.SourceId, label = "Notion",
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
	/// <c>source-doc</c> (tagged by <paramref name="id"/>, keyed by <paramref name="target"/>) carrying the rich
	/// <c>html</c> the SourceView renders plus the <c>text</c> (Claude's channel). A missing token or fetch failure toasts.
	/// </summary>
	private async Task FetchSourceForWebAsync(string id, string target) {
		if (string.IsNullOrWhiteSpace(target)) {
			return;
		}

		SourceDoc doc;
		try {
			doc = await _sources.FetchAsync(target, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
			Notify("error", $"Couldn't open that source: {ex.Message}");
			return;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "source-doc", id, target, title = doc.Title, text = doc.Text, html = doc.Html,
		}));
	}
}
