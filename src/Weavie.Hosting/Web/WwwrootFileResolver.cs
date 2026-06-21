namespace Weavie.Hosting.Web;

/// <summary>The outcome of resolving a wwwroot request: whether a file was found, plus its bytes + MIME type
/// (on a 404, <see cref="Found"/> is false and the bytes/MIME are a small text/plain "Not Found" body).</summary>
public readonly record struct AssetResponse(bool Found, byte[] Bytes, string Mime);

/// <summary>
/// Resolves a webview request path against an on-disk <c>wwwroot</c>: maps <c>/</c> to <c>index.html</c>, blocks
/// path-traversal escapes, reads the file bytes, and guesses the MIME type. Shared so the Mac (WKWebView) and
/// Linux (WebKitGTK) <c>app://</c> scheme handlers — and any future host serving from disk — don't each
/// re-implement (and drift on) the same resolution + MIME map; each host supplies only its native
/// request/response binding and decides how to emit a 404 (a body, or a native fail).
/// </summary>
public sealed class WwwrootFileResolver {
	private static readonly byte[] NotFoundBody = "Not Found"u8.ToArray();

	/// <summary>Creates a resolver serving files from <paramref name="wwwroot"/> (resolved to a full path).</summary>
	public WwwrootFileResolver(string wwwroot) {
		ArgumentException.ThrowIfNullOrEmpty(wwwroot);
		Root = Path.GetFullPath(wwwroot);
	}

	/// <summary>The resolved web root (full path).</summary>
	public string Root { get; }

	/// <summary>
	/// Resolves <paramref name="requestPath"/> (e.g. <c>/index.html</c>, <c>/assets/app.js</c>, or <c>/</c>) to a
	/// file under the web root. Returns the bytes + MIME on success; on a path that escapes the root or a missing
	/// file, returns <see cref="AssetResponse.Found"/> = false with a text/plain "Not Found" body.
	/// </summary>
	public AssetResponse Resolve(string requestPath) {
		string path = string.IsNullOrEmpty(requestPath) || requestPath == "/" ? "/index.html" : requestPath;
		string resolved = Path.GetFullPath(Path.Combine(Root, path.TrimStart('/')));

		// Defend against path traversal escaping the web root.
		bool insideRoot = resolved == Root
			|| resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
		if (!insideRoot || !File.Exists(resolved)) {
			return new AssetResponse(Found: false, NotFoundBody, "text/plain");
		}

		return new AssetResponse(Found: true, File.ReadAllBytes(resolved), MimeFor(Path.GetExtension(resolved)));
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
