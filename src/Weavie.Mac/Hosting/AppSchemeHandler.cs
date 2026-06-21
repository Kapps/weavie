using Foundation;
using Weavie.Hosting.Web;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Serves the built web app to the WKWebView over a custom <c>app://</c> scheme, straight from the bundle's
/// <c>Resources/wwwroot</c> — no network, no localhost port, secure same-origin context (so workers and the
/// Event Timing API behave). Path resolution + MIME come from the shared <see cref="WwwrootFileResolver"/>;
/// this owns only the native scheme-task binding and the 404 fail.
/// </summary>
public sealed class AppSchemeHandler : NSObject, IWKUrlSchemeHandler {
	private readonly WwwrootFileResolver _resolver;

	/// <summary>Creates a handler serving files from <paramref name="wwwroot"/>.</summary>
	public AppSchemeHandler(string wwwroot) {
		_resolver = new WwwrootFileResolver(wwwroot);
	}

	/// <summary>Serves the requested <c>app://</c> URL, or fails the task on a 404.</summary>
	[Export("webView:startURLSchemeTask:")]
	public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask) {
		var url = urlSchemeTask.Request.Url;
		var response = _resolver.Resolve(url?.Path ?? "/");
		if (!response.Found) {
			FailNotFound(urlSchemeTask, url);
			return;
		}

		using var data = NSData.FromArray(response.Bytes);
		using var urlResponse = new NSUrlResponse(url!, response.Mime, (nint)response.Bytes.Length, "utf-8");
		urlSchemeTask.DidReceiveResponse(urlResponse);
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
}
