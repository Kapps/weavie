namespace Weavie.Core.Sources;

/// <summary>
/// The host-facing source operations — resolve/where-to-get-a-token, save a pasted token (validating it first),
/// and fetch a target — behind an interface so the headless harness can swap a deterministic stand-in (the source
/// analogue of <c>IPullRequestProvider</c> / <c>StaticPullRequestProvider</c>) for an offline connect/fetch journey.
/// </summary>
public interface ISourceConnector {
	/// <summary>True when a registered source claims <paramref name="target"/> — the host-side open resolver, so the
	/// web hands a URL to the host rather than re-implementing each source's <see cref="ISource.Match"/>.</summary>
	bool Matches(string target);

	/// <summary>Where the user creates an access token for <paramref name="sourceId"/> (opened in the browser on connect).</summary>
	string SetupUrlFor(string sourceId);

	/// <summary>
	/// Validates <paramref name="token"/> for <paramref name="sourceId"/> and, if accepted, persists it; returns
	/// the authorized workspace name. Throws <see cref="InvalidOperationException"/> when the token is rejected, so
	/// an invalid token is never saved.
	/// </summary>
	Task<string> SaveTokenAsync(string sourceId, string token, CancellationToken ct = default);

	/// <summary>Fetches <paramref name="target"/> via the source that matches it, using its saved token.</summary>
	Task<SourceDoc> FetchAsync(string target, CancellationToken ct = default);
}
