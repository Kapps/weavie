namespace Weavie.Core.Sources;

/// <summary>
/// A read-only third-party document projection from one fetch: <paramref name="Title"/> for the tab + page header,
/// <paramref name="Markdown"/> the document's content as (enhanced) markdown — the single source for both the
/// shadow-root SourceView (rendered to HTML web-side) and Claude's reading channel — and <paramref name="EditedTime"/>
/// the last-edited time for the header. The per-document icon + properties land with a later slice.
/// </summary>
/// <param name="Title">The document's title.</param>
/// <param name="Markdown">The document's content as markdown (rendered for display; also Claude's channel).</param>
/// <param name="EditedTime">The document's last-edited time (ISO 8601), or empty when unknown.</param>
/// <param name="Truncated">True when the source cut the content off (e.g. Notion's per-page block limit) — the web
/// renders the loss as a banner. Kept out of <paramref name="Markdown"/> so it stays the verbatim fetched text the
/// write path diffs against.</param>
/// <param name="UnknownBlocks">How many blocks the source couldn't read (0 when whole) — rendered with the banner.</param>
public sealed record SourceDoc(string Title, string Markdown, string EditedTime, bool Truncated, int UnknownBlocks);

/// <summary>
/// A registered source plugin: it matches a target (URL/id), validates the user's access token, and fetches a
/// target into a <see cref="SourceDoc"/>. Auth is a user-supplied personal access token (the host stores it and
/// hands it back); the source only knows how to validate one and fetch with it. The host owns presentation,
/// routing, and the token file; a source is thin — match + validate + fetch + map. See
/// <c>docs/specs/notion-source-auth.md</c>.
/// </summary>
public interface ISource {
	/// <summary>The stable source id (e.g. <c>notion</c>) — the credential key and the routing tag.</summary>
	string Id { get; }

	/// <summary>Where the user creates an access token — opened in their browser when they connect.</summary>
	string SetupUrl { get; }

	/// <summary>True when <paramref name="target"/> (a URL) belongs to this source — the routing predicate.</summary>
	bool Match(string target);

	/// <summary>
	/// Validates <paramref name="accessToken"/> against the source's API and returns the authorized workspace's
	/// display name (may be empty). Throws <see cref="InvalidOperationException"/> when the token is rejected.
	/// </summary>
	Task<string> ValidateAsync(string accessToken, CancellationToken ct = default);

	/// <summary>Fetches <paramref name="target"/> into a <see cref="SourceDoc"/>, authenticated by <paramref name="accessToken"/>.</summary>
	Task<SourceDoc> FetchAsync(string target, string accessToken, CancellationToken ct = default);

	/// <summary>
	/// Applies one exact-match content edit to <paramref name="target"/> — <paramref name="oldStr"/> (which must
	/// match the document exactly once) replaced by <paramref name="newStr"/>, both diffed against the verbatim
	/// fetched markdown — and returns the refreshed <see cref="SourceDoc"/> from the update's response. Throws
	/// <see cref="SourceConflictException"/> when the document changed since it was fetched (the match failed).
	/// </summary>
	Task<SourceDoc> UpdateAsync(string target, string accessToken, string oldStr, string newStr, CancellationToken ct = default);
}
