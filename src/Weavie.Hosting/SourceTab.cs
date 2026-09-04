namespace Weavie.Hosting;

// The one place the read-only source-tab wire shapes live. Every producer — a fetched Notion doc, the log
// viewer, the corrections analysis — publishes the same `loading` / `document` / `error` states into the
// session's `sources` feature, keyed by target, as STATE so a reconnecting client replays them into the tab.
internal static class SourceTab {
	// Opens (or re-opens) a target's tab in its spinner state, titled before its content exists.
	public static void Loading(HostSession session, string target, string title, string sourceId) =>
		session.State.Set("sources", target, "loading", new { target, title, sourceId });

	// A host-rendered document: html the web injects as-is (sanitized downstream), with no editable source.
	public static void Html(HostSession session, string target, string title, string sourceId, string html) =>
		session.State.Set("sources", target, "document", new {
			target,
			title,
			html,
			editedTime = "",
			sourceId,
		});

	// Resolves a spinner with the reason it could not become a document.
	public static void Error(HostSession session, string target, string message) =>
		session.State.Set("sources", target, "error", new { target, message });
}
