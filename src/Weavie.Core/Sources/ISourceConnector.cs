namespace Weavie.Core.Sources;

/// <summary>A registered source's routing descriptor: its id and the host patterns it claims. Pushed to the web.</summary>
/// <param name="Id">The stable source id (e.g. <c>notion</c>).</param>
/// <param name="Hosts">The host patterns it claims (exact, or a <c>*.</c> subdomain wildcard).</param>
public sealed record SourceDescriptor(string Id, IReadOnlyList<string> Hosts);

/// <summary>
/// The host-facing source operations — where to get a token, save a pasted token (validating it first), and fetch
/// a target — behind an interface so the headless harness can swap a deterministic stand-in (the source analogue
/// of <c>IPullRequestProvider</c> / <c>StaticPullRequestProvider</c>) for an offline connect/fetch journey.
/// </summary>
public interface ISourceConnector {
	/// <summary>The registered sources' routing descriptors (id + host patterns), pushed to the web so its open
	/// resolver routes a matching URL to the native renderer from one declaration — never a hardcoded copy.</summary>
	IReadOnlyList<SourceDescriptor> Sources { get; }

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
