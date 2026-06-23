using System.Globalization;
using Foundation;
using Weavie.Hosting.Web;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Serves the built web app to the WKWebView over a custom <c>app://</c> scheme from the bundle's
/// <c>Resources/wwwroot</c> — no network, secure same-origin context (so workers + the Event Timing API behave).
/// Path resolution + MIME come from the shared <see cref="WwwrootFileResolver"/>; this owns the scheme-task
/// binding and the HTTP response it answers with.
/// </summary>
public sealed class AppSchemeHandler : NSObject, IWKUrlSchemeHandler {
	private readonly WwwrootFileResolver _resolver;

	/// <summary>Creates a handler serving files from <paramref name="wwwroot"/>.</summary>
	public AppSchemeHandler(string wwwroot) {
		_resolver = new WwwrootFileResolver(wwwroot);
	}

	/// <summary>Serves the requested <c>app://</c> URL as an HTTP response (200, or 404 when the file is missing).</summary>
	[Export("webView:startURLSchemeTask:")]
	public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
		var url = urlSchemeTask.Request.Url ?? new NSUrl("app://app/");
		var asset = _resolver.Resolve(url.Path ?? "/");

		// Answer with a real NSHTTPURLResponse, not a bare NSURLResponse: for fetch()/XHR WebKit otherwise
		// synthesizes a response with status 0, so monaco-vscode-api's `status !== 200` theme/grammar reads throw
		// and every bit of syntax highlighting silently dies. A 200 (+ permissive CORS, since a custom scheme can
		// be treated as a cross-origin/opaque fetch) makes these same-origin loads behave like a normal server.
		// Plain subresource (<script>/<link>/font) loads tolerate either, which is why the app still boots without it.
		using var headers = new NSMutableDictionary {
			[(NSString)"Content-Type"] = (NSString)asset.Mime,
			[(NSString)"Content-Length"] = (NSString)asset.Bytes.Length.ToString(CultureInfo.InvariantCulture),
			[(NSString)"Access-Control-Allow-Origin"] = (NSString)"*",
		};
		using var response = new NSHttpUrlResponse(url, (nint)(asset.Found ? 200 : 404), "HTTP/1.1", headers);
		using var data = NSData.FromArray(asset.Bytes);
		urlSchemeTask.DidReceiveResponse(response);
		urlSchemeTask.DidReceiveData(data);
		urlSchemeTask.DidFinish();
	}

	/// <summary>No-op: reads complete synchronously in <see cref="StartUrlSchemeTask"/>, so there is nothing to cancel.</summary>
	[Export("webView:stopURLSchemeTask:")]
	public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
	}
}
