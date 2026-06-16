using Foundation;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Serves the built Solid/Vite web app to the WKWebView over a custom <c>app://</c> scheme,
/// straight from the bundle's <c>Resources/wwwroot</c> — no network, no localhost port, secure
/// same-origin context (so workers and the Event Timing API behave). Files are read from disk
/// and returned with a correct MIME type (WebKit refuses ES modules served with the wrong type).
/// </summary>
public sealed class AppSchemeHandler : NSObject, IWKUrlSchemeHandler {
	private readonly string _root;

	/// <summary>Creates a handler that serves files from <paramref name="wwwroot"/> (resolved to a full path).</summary>
	public AppSchemeHandler(string wwwroot) {
		ArgumentException.ThrowIfNullOrEmpty(wwwroot);
		_root = Path.GetFullPath(wwwroot);
	}

	/// <summary>
	/// Serves the requested <c>app://</c> URL from the web root: maps "/" to index.html, rejects
	/// path-traversal escapes, and returns the file bytes with a correct MIME type (404 otherwise).
	/// </summary>
	[Export("webView:startURLSchemeTask:")]
	public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
		var url = urlSchemeTask.Request.Url;
		var requestedPath = url?.Path;
		if (string.IsNullOrEmpty(requestedPath) || requestedPath == "/") {
			requestedPath = "/index.html";
		}

		var resolved = Path.GetFullPath(Path.Combine(_root, requestedPath.TrimStart('/')));

		// Defend against path traversal escaping the web root.
		if (!resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			&& resolved != _root) {
			FailNotFound(urlSchemeTask, url);
			return;
		}

		if (!File.Exists(resolved)) {
			FailNotFound(urlSchemeTask, url);
			return;
		}

		var bytes = File.ReadAllBytes(resolved);
		using var data = NSData.FromArray(bytes);
		var mime = MimeFor(Path.GetExtension(resolved));
		using var response = new NSUrlResponse(url!, mime, (nint)bytes.Length, "utf-8");

		urlSchemeTask.DidReceiveResponse(response);
		urlSchemeTask.DidReceiveData(data);
		urlSchemeTask.DidFinish();
	}

	/// <summary>No-op: reads complete synchronously in <see cref="StartUrlSchemeTask"/>, so there is nothing to cancel.</summary>
	[Export("webView:stopURLSchemeTask:")]
	public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
		// Synchronous file reads complete within StartUrlSchemeTask; nothing to cancel.
	}

	private static void FailNotFound(IWKUrlSchemeTask task, NSUrl? url) {
		using var response = new NSUrlResponse(url ?? new NSUrl("app://app/"), "text/plain", 0, "utf-8");
		task.DidReceiveResponse(response);
		task.DidFinish();
	}

	private static string MimeFor(string extension) => extension.ToLowerInvariant() switch {
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
