namespace Weavie.Core.Sources;

/// <summary>
/// The host-facing source operations — resolve/where-to-get-a-token, save a pasted token (validating it first),
/// and fetch a target — behind an interface so the headless harness can swap a deterministic stand-in (the source
/// analogue of <c>IPullRequestProvider</c> / <c>StaticPullRequestProvider</c>) for an offline connect/fetch journey.
/// </summary>
public interface ISourceConnector {
	/// <summary>The id of the registered source that claims <paramref name="target"/> (null when none does) — the
	/// host-side open resolver and the identity stamped on source messages, so the web never re-implements
	/// each source's <see cref="ISource.Match"/>.</summary>
	string? IdFor(string target);

	/// <summary>True when the source that claims <paramref name="target"/> already has a saved token, so a fetch can run
	/// without first sending the user through connect. False routes an opened source URL to the connect prompt, not a blank tab.</summary>
	bool IsConnected(string target);

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

	/// <summary>
	/// Applies one exact-match content edit (<paramref name="oldStr"/> → <paramref name="newStr"/>, diffed web-side
	/// against the verbatim fetched markdown) to <paramref name="target"/> via the source that matches it, and
	/// returns the refreshed <see cref="SourceDoc"/>. Throws <see cref="SourceConflictException"/> when the
	/// document changed since it was fetched.
	/// </summary>
	Task<SourceDoc> UpdateAsync(string target, string oldStr, string newStr, CancellationToken ct = default);
}
