namespace Weavie.Core.Sources;

/// <summary>
/// A read-only third-party document projection from one fetch: <paramref name="Title"/> for the tab,
/// <paramref name="Html"/> the rich display projection rendered in the shadow-root SourceView, and
/// <paramref name="Text"/> a clean markdown projection (Claude's reading channel). The per-document icon lands
/// with a later slice.
/// </summary>
/// <param name="Title">The document's title.</param>
/// <param name="Text">A markdown projection of the document's content (for Claude).</param>
/// <param name="Html">A semantic-HTML projection of the document's content (for display), escaped at the source.</param>
public sealed record SourceDoc(string Title, string Text, string Html);

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

	/// <summary>
	/// The host patterns this source claims (an exact host, or a <c>*.</c> subdomain wildcard) — its declared
	/// routing predicate. Surfaced to the web so opening a matching URL renders here, from this one declaration
	/// rather than a hardcoded copy. e.g. <c>notion.so</c>, <c>*.notion.so</c>.
	/// </summary>
	IReadOnlyList<string> Hosts { get; }

	/// <summary>True when <paramref name="target"/> (an http(s) URL) belongs to this source — implement via <see cref="SourceHostMatch"/> over <see cref="Hosts"/>.</summary>
	bool Match(string target);

	/// <summary>
	/// Validates <paramref name="accessToken"/> against the source's API and returns the authorized workspace's
	/// display name (may be empty). Throws <see cref="InvalidOperationException"/> when the token is rejected.
	/// </summary>
	Task<string> ValidateAsync(string accessToken, CancellationToken ct = default);

	/// <summary>Fetches <paramref name="target"/> into a <see cref="SourceDoc"/>, authenticated by <paramref name="accessToken"/>.</summary>
	Task<SourceDoc> FetchAsync(string target, string accessToken, CancellationToken ct = default);
}
