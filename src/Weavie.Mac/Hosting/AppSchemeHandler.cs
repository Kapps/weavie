using Foundation;
using Weavie.Hosting.Web;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Serves the built web app to the WKWebView over a custom <c>app://</c> scheme from the bundle's
/// <c>Resources/wwwroot</c> — no network, secure same-origin context (so workers + the Event Timing API behave).
/// The HTTP contract (status, MIME, headers) comes from the shared <see cref="WwwrootFileResolver"/>; this only
/// marshals it onto the native scheme task.
/// </summary>
public sealed class AppSchemeHandler : NSObject, IWKUrlSchemeHandler {
	private readonly WwwrootFileResolver _resolver;

	/// <summary>Creates a handler serving files from <paramref name="wwwroot"/>.</summary>
	public AppSchemeHandler(string wwwroot) {
		_resolver = new WwwrootFileResolver(wwwroot);
	}

	/// <summary>Serves the requested <c>app://</c> URL as the HTTP response the resolver built.</summary>
	[Export("webView:startURLSchemeTask:")]
	public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
		var url = urlSchemeTask.Request.Url ?? new NSUrl("app://app/");
		var asset = _resolver.Resolve(url.Path ?? "/");

		// A real NSHTTPURLResponse is required: for fetch() a bare NSURLResponse yields status 0, so
		// monaco-vscode-api's `status !== 200` grammar/theme reads throw and all highlighting dies.
		using var headers = new NSMutableDictionary {
			[(NSString)"Content-Type"] = (NSString)asset.ContentType,
		};
		foreach (var (name, value) in asset.Headers) {
			headers[(NSString)name] = (NSString)value;
		}

		using var response = new NSHttpUrlResponse(url, (nint)asset.StatusCode, "HTTP/1.1", headers);
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
