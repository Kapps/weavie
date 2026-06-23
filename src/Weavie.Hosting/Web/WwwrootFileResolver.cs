namespace Weavie.Hosting.Web;

/// <summary>
/// The HTTP response a wwwroot request resolves to (status, body, content type, headers) — built once here so the
/// native <c>app://</c> handlers only marshal it instead of each deciding the contract.
/// </summary>
public readonly record struct AssetResponse(
	int StatusCode, byte[] Bytes, string ContentType, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>
/// Resolves a webview request path against an on-disk <c>wwwroot</c>: maps <c>/</c> to <c>index.html</c>, blocks
/// path-traversal, reads the bytes, and builds the full HTTP response (status + MIME + headers). Shared by the
/// Mac/Linux <c>app://</c> scheme handlers; each host supplies only its native request/response binding.
/// </summary>
public sealed class WwwrootFileResolver {
	private static readonly byte[] NotFoundBody = "Not Found"u8.ToArray();

	// Apple's WebKit treats an in-scheme fetch as cross-origin with a null Origin (webkit.org bug 205198), so only
	// `*` is honored; safe because app:// is unreachable outside our WebView and serves only public bundle assets.
	private static readonly IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders =
		[new("Access-Control-Allow-Origin", "*")];

	/// <summary>Creates a resolver serving files from <paramref name="wwwroot"/> (resolved to a full path).</summary>
	public WwwrootFileResolver(string wwwroot) {
		ArgumentException.ThrowIfNullOrEmpty(wwwroot);
		Root = Path.GetFullPath(wwwroot);
	}

	/// <summary>The resolved web root (full path).</summary>
	public string Root { get; }

	/// <summary>
	/// Resolves <paramref name="requestPath"/> to a file under the web root, returning a <c>200</c> response with
	/// its bytes + MIME. A path escaping the root or a missing file returns <c>404</c> with a "Not Found" body.
	/// </summary>
	public AssetResponse Resolve(string requestPath) {
		string path = string.IsNullOrEmpty(requestPath) || requestPath == "/" ? "/index.html" : requestPath;
		string resolved = Path.GetFullPath(Path.Combine(Root, path.TrimStart('/')));

		// Defend against path traversal escaping the web root.
		bool insideRoot = resolved == Root
			|| resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
		if (!insideRoot || !File.Exists(resolved)) {
			return new AssetResponse(404, NotFoundBody, "text/plain", ResponseHeaders);
		}

		return new AssetResponse(200, File.ReadAllBytes(resolved), MimeFor(Path.GetExtension(resolved)), ResponseHeaders);
	}

	/// <summary>The MIME type for a file extension (WebKit refuses ES modules served with the wrong type).</summary>
	public static string MimeFor(string extension) => extension.ToLowerInvariant() switch {
		".html" => "text/html",
		".js" or ".mjs" => "text/javascript",
		".css" => "text/css",
		".json" or ".map" => "application/json",
		".svg" => "image/svg+xml",
		".png" => "image/png",
		".woff" => "font/woff",
		".woff2" => "font/woff2",
		".ttf" => "font/ttf",
		".wasm" => "application/wasm",
		_ => "application/octet-stream",
	};
}
