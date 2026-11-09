using Weavie.Core.Credentials;

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
/// A registered source plugin: it claims link hosts and fetches a target into a <see cref="SourceDoc"/>,
/// authenticated by the key its <see cref="Credential"/> declares. The host owns presentation, routing, and the key;
/// a source is thin — match + fetch + map. See <c>docs/specs/notion-source-auth.md</c>.
/// </summary>
public interface ISource {
	/// <summary>The service whose key authenticates this source; its id is the source id and routing tag.</summary>
	CredentialDefinition Credential { get; }

	/// <summary>The hosts whose http(s) URLs this source opens — the routing rule, applied by <see cref="SourceLinks.Claims"/>.
	/// An entry with a leading dot claims every subdomain of it.</summary>
	IReadOnlyList<string> LinkHosts { get; }

	/// <summary>Fetches <paramref name="target"/> into a <see cref="SourceDoc"/>, authenticated by <paramref name="accessToken"/>.</summary>
	Task<SourceDoc> FetchAsync(string target, CredentialSecret accessToken, CancellationToken ct = default);

	/// <summary>
	/// Applies one exact-match content edit to <paramref name="target"/> — <paramref name="oldStr"/> (which must
	/// match the document exactly once) replaced by <paramref name="newStr"/>, both diffed against the verbatim
	/// fetched markdown — and returns the refreshed <see cref="SourceDoc"/> from the update's response. Throws
	/// <see cref="SourceConflictException"/> when the document changed since it was fetched (the match failed).
	/// </summary>
	Task<SourceDoc> UpdateAsync(string target, CredentialSecret accessToken, string oldStr, string newStr, CancellationToken ct = default);
}
