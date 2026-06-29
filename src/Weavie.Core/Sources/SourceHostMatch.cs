namespace Weavie.Core.Sources;

/// <summary>
/// The shared host-pattern matcher behind <see cref="ISource.Match"/>: a target URL belongs to a source when its
/// host equals a declared pattern or matches a <c>*.</c> subdomain wildcard. One algorithm, so every source routes
/// the same way and only declares its <see cref="ISource.Hosts"/>.
/// </summary>
internal static class SourceHostMatch {
	/// <summary>True when <paramref name="target"/> is an http(s) URL whose host matches one of <paramref name="hosts"/>.</summary>
	public static bool Matches(IReadOnlyList<string> hosts, string target) {
		if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
			return false;
		}

		foreach (string pattern in hosts) {
			bool match = pattern.StartsWith("*.", StringComparison.Ordinal)
				? uri.Host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
				: uri.Host.Equals(pattern, StringComparison.OrdinalIgnoreCase);
			if (match) {
				return true;
			}
		}

		return false;
	}
}
