namespace Weavie.Core.Sources;

/// <summary>
/// The single escaping/sanitization source of truth for source-doc HTML: every piece of API-derived text,
/// attribute value, and URL passes through here before it is concatenated into markup. The Notion payload is
/// untrusted external data, so the mappers never interpolate a raw API string into HTML.
/// </summary>
internal static class HtmlEscape {
	/// <summary>Escapes element text content (<c>&amp; &lt; &gt; " '</c>).</summary>
	public static string Text(string value) {
		ArgumentNullException.ThrowIfNull(value);
		return value
			.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal)
			.Replace("\"", "&quot;", StringComparison.Ordinal)
			.Replace("'", "&#39;", StringComparison.Ordinal);
	}

	/// <summary>Escapes an (always-quoted) attribute value — same set as <see cref="Text"/>.</summary>
	public static string Attribute(string value) => Text(value);

	/// <summary>
	/// A safe href/src, or <c>null</c> when the URL isn't an absolute <c>http</c>/<c>https</c>/<c>mailto</c> — so a
	/// <c>javascript:</c>/<c>data:</c>/<c>vbscript:</c> or unparseable URL is dropped, never emitted. The returned
	/// value is attribute-escaped.
	/// </summary>
	public static string? SafeUrl(string value) {
		ArgumentNullException.ThrowIfNull(value);
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) {
			return null;
		}

		bool allowed = uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto;
		return allowed ? Attribute(value) : null;
	}
}
