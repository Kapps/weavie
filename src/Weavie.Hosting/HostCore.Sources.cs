using Weavie.Core.Editor;
using Weavie.Core.Sources;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The source system's host wiring: route an opened URL to its source (connecting it first when needed) and fetch a
// target into a read-only editor tab. The fetch lives in Core (SourceConnector); this only bridges it to the page.
// See docs/specs/web-and-source-tabs.md.
public sealed partial class HostCore {
	/// <summary>
	/// The open resolver: the page hands every opened URL here, and the host — which owns the sources and their
	/// <see cref="ISource.LinkHosts"/> rule — decides. A claimed URL is fetched + rendered natively when its source is
	/// connected, else it routes to the connect prompt (remembered, so it opens once connected); anything else is
	/// sent back as <c>open-web</c> for a web (iframe) tab. The host owns which sources are connected; the web only
	/// pre-routes clicked links with the connected sources' hosts it was sent.
	/// </summary>
	private void OpenTargetForWeb(HostSession session, string url) {
		if (!IsHttpUrl(url)) {
			return;
		}

		if (_sources.IdFor(url) is not { } sourceId) {
			session.OpenEditorOverlay(url, "web", EditorOpenIntent.Navigation);
		} else if (_sources.IsConnected(url)) {
			OpenSourceTab(session, url, sourceId);
		} else {
			PromptConnect(session, sourceId, owner => OpenSourceTab(owner, url, sourceId));
		}
	}

	private void OpenSourceTab(HostSession session, string url, string sourceId) {
		session.OpenEditorOverlay(url, "source", EditorOpenIntent.Navigation);
		_ = session.Background.Run(ct => FetchSourceForWebAsync(session, url, sourceId, ct));
	}

	/// <summary>
	/// Fetches a source <paramref name="target"/> (the matching source must be connected) and posts the host→web
	/// <c>source-doc</c> (keyed by <paramref name="target"/>, stamped with <paramref name="sourceId"/> — the web
	/// keys the tab icon off it) carrying the <c>markdown</c> the SourceView renders to HTML (and Claude reads).
	/// <c>source-loading</c> is posted first so the tab opens with a spinner while the fetch runs; a fetch failure
	/// posts <c>source-error</c> into that same tab.
	/// </summary>
	private async Task FetchSourceForWebAsync(
		HostSession session,
		string target,
		string sourceId,
		CancellationToken ct) {
		session.State.Set("sources", target, "loading", WireJson.Default.SourceLoading, new(target, GuessSourceTitle(target), sourceId));
		SourceDoc doc;
		try {
			doc = await _sources.FetchAsync(target, ct).ConfigureAwait(false);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			return;
		} catch (Exception ex) {
			// The spinner is already up, so EVERY failure must resolve it — not just the http/cancel set: a non-JSON
			// 200 (proxy / captive-portal / incident HTML) throws JsonException deeper in, and this is fire-and-forget,
			// so anything uncaught would leave the tab spinning forever. Surfaced loudly in the tab, never swallowed.
			session.State.Set("sources", target, "error", WireJson.Default.SourceLoadError, new(target, ex.Message));
			return;
		}

		PostSourceDoc(session, target, sourceId, doc, string.Empty);
	}

	// Saved source tabs reopen without their content, which lived in the prior session's state; refill each from its
	// owner so no restored tab waits on a document nothing will send.
	private void RestoreSourceDocuments(HostSession session) {
		foreach (var entry in session.EditorSession.Open.Where(entry => entry.Kind == "source")) {
			string target = entry.Path;
			if (target == LogsTarget) {
				FillLogsDocument(session);
			} else if (_sources.IdFor(target) is { } sourceId && _sources.IsConnected(target)) {
				_ = session.Background.Run(ct => FetchSourceForWebAsync(session, target, sourceId, ct));
			} else {
				session.State.Set("sources", target, "error", WireJson.Default.SourceLoadError, new(
					target,
					"This source isn't connected. Open the link again to connect it."));
			}
		}
	}

	// The one session event for a fetched/updated SourceDoc — fetch and save both land here, so the web's
	// store always sees the same shape (including the loss flags its banner renders).
	private static void PostSourceDoc(HostSession session, string target, string sourceId, SourceDoc doc, string editId) =>
		session.State.SetVersioned("sources", target, "document", WireJson.Default.SourceDocument, revision => new(
			target,
			doc.Title,
			doc.Markdown,
			editId,
			revision,
			doc.EditedTime,
			sourceId,
			doc.Truncated,
			doc.UnknownBlocks));

	/// <summary>
	/// Applies one block edit (the <c>source-save-edit</c> message: an exact-match old/new pair the web diffed
	/// against the verbatim fetched markdown) and re-posts the refreshed <c>source-doc</c> from the update's
	/// response. A conflict — the page changed in Notion since the fetch — posts <c>source-edit-error</c> with
	/// <c>stale:true</c> so the block offers a re-fetch; any other failure posts <c>stale:false</c>. This is
	/// fire-and-forget like the fetch: every outcome must resolve the block's saving state, never leave it stuck.
	/// </summary>
	private async Task SaveSourceEditAsync(
		HostSession session,
		string target,
		string oldStr,
		string newStr,
		string editId,
		CancellationToken ct) {
		try {
			var doc = await _sources.UpdateAsync(target, oldStr, newStr, ct).ConfigureAwait(false);
			// UpdateAsync just matched a source for this target, so a missing id is a real invariant break.
			string sourceId = _sources.IdFor(target) ?? throw new InvalidOperationException($"No source claims '{target}'.");
			PostSourceDoc(session, target, sourceId, doc, editId);
		} catch (SourceConflictException ex) {
			PostSourceEditError(session, target, editId, ex.Message, stale: true);
		} catch (Exception ex) {
			PostSourceEditError(session, target, editId, ex.Message, stale: false);
		}
	}

	private static void PostSourceEditError(
		HostSession session,
		string target,
		string editId,
		string message,
		bool stale) =>
		session.Bus.Feature("sources").Publish("editError", WireJson.Default.SourceEditError, new(target, message, editId, stale));

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

internal sealed record SourceEditError(string Target, string Message, string EditId, bool Stale);

internal sealed record SourceLinkHosts(string[] Hosts);

internal sealed record SourceLoading(string Target, string Title, string SourceId);

internal sealed record SourceLoadError(string Target, string Message);

internal sealed record SourceDocument(
	string Target,
	string Title,
	string Markdown,
	string EditId,
	long Revision,
	string EditedTime,
	string SourceId,
	bool Truncated,
	int UnknownBlocks);
