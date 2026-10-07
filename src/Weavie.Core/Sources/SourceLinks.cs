namespace Weavie.Core.Sources;

/// <summary>
/// The one link-claiming rule: a source declares the hosts it opens (<see cref="ISource.LinkHosts"/>), and an http(s)
/// URL belongs to it when its host equals one, or — for an entry with a leading dot — is any subdomain of it. The web
/// applies the same rule to the connected sources' hosts so a click routes without a host round-trip.
/// </summary>
public static class SourceLinks {
	/// <summary>True when <paramref name="url"/> is an http(s) URL on one of <paramref name="hosts"/>.</summary>
	public static bool Claims(IReadOnlyList<string> hosts, string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri)
		&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
		&& hosts.Any(host => host.StartsWith('.')
			? uri.Host.EndsWith(host, StringComparison.OrdinalIgnoreCase)
			: uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
}
