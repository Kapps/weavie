namespace Weavie.Core.Sources;

/// <summary>
/// A read-only third-party document projection: <paramref name="Title"/> for the tab, <paramref name="Text"/> a
/// clean markdown projection (what the human reads and what Claude reads — both from one fetch). The presentation
/// <c>html</c>/<c>icon</c> fields land with the shadow-root SourceView; the auth slice needs only title + text.
/// </summary>
/// <param name="Title">The document's title.</param>
/// <param name="Text">A markdown projection of the document's content.</param>
public sealed record SourceDoc(string Title, string Text);

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
}
