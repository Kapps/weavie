using Weavie.Core.Credentials;

namespace Weavie.Core.Sources;

/// <summary>
/// The host-facing entry point to the source system: owns the registered sources and fetches with each one's key
/// from <see cref="CredentialService"/>, so <c>HostCore</c> drives fetch/update through one injected dependency.
/// </summary>
public sealed class SourceConnector : ISourceConnector {
	private readonly IReadOnlyList<ISource> _sources;
	private readonly CredentialService _credentials;

	/// <summary>Creates a connector over <paramref name="sources"/>, authenticated by keys from <paramref name="credentials"/>.</summary>
	public SourceConnector(IReadOnlyList<ISource> sources, CredentialService credentials) {
		ArgumentNullException.ThrowIfNull(sources);
		ArgumentNullException.ThrowIfNull(credentials);
		_sources = sources;
		_credentials = credentials;
	}

	/// <inheritdoc/>
	public string? IdFor(string target) => Claiming(target)?.Credential.Id;

	/// <inheritdoc/>
	public bool IsConnected(string target) => Claiming(target) is { } source && _credentials.IsConnected(source.Credential.Id);

	/// <inheritdoc/>
	public IReadOnlyList<string> ConnectedLinkHosts() =>
		[.. _sources.Where(source => _credentials.IsConnected(source.Credential.Id)).SelectMany(source => source.LinkHosts)];

	/// <summary>
	/// Fetches <paramref name="target"/> via the source that matches it, using its saved key. Throws when no
	/// source matches or the source isn't connected yet (the host turns that into a "connect first" prompt).
	/// </summary>
	public async Task<SourceDoc> FetchAsync(string target, CancellationToken ct = default) {
		var (source, key) = ConnectedSource(target);
		return await source.FetchAsync(target, key, ct).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public async Task<SourceDoc> UpdateAsync(string target, string oldStr, string newStr, CancellationToken ct = default) {
		var (source, key) = ConnectedSource(target);
		return await source.UpdateAsync(target, key, oldStr, newStr, ct).ConfigureAwait(false);
	}

	private (ISource Source, CredentialSecret Key) ConnectedSource(string target) {
		var source = Claiming(target)
			?? throw new InvalidOperationException($"No connected source can open '{target}'.");
		var key = _credentials.Read(source.Credential.Id)
			?? throw new InvalidOperationException($"Connect {source.Credential.DisplayName} first — there's no token for it.");
		return (source, key);
	}

	private ISource? Claiming(string target) => _sources.FirstOrDefault(s => SourceLinks.Claims(s.LinkHosts, target));
}
