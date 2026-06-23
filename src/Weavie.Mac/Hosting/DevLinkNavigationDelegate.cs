// Debug-only; compiled out in Release (no dev server) to stay dead-code-free under the zero-warning gate.
#if DEBUG
using Weavie.Hosting.Web;
using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// A <see cref="WKNavigationDelegate"/> that cancels the dev-server error page's <c>weavie-dev://</c> links and
/// invokes the matching host handler (Retry / Load stale bundle), allowing every other navigation through.
/// </summary>
internal sealed class DevLinkNavigationDelegate : WKNavigationDelegate {
	private readonly Action _onRetry;
	private readonly Action _onLoadBundle;

	public DevLinkNavigationDelegate(Action onRetry, Action onLoadBundle) {
		ArgumentNullException.ThrowIfNull(onRetry);
		ArgumentNullException.ThrowIfNull(onLoadBundle);
		_onRetry = onRetry;
		_onLoadBundle = onLoadBundle;
	}

	public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler) {
		string url = navigationAction.Request?.Url?.AbsoluteString ?? string.Empty;
		if (url.StartsWith(DevWebBringUp.RetryUrl, StringComparison.OrdinalIgnoreCase)) {
			decisionHandler(WKNavigationActionPolicy.Cancel);
			_onRetry();
			return;
		}

		if (url.StartsWith(DevWebBringUp.BundleUrl, StringComparison.OrdinalIgnoreCase)) {
			decisionHandler(WKNavigationActionPolicy.Cancel);
			_onLoadBundle();
			return;
		}

		decisionHandler(WKNavigationActionPolicy.Allow);
	}
}
#endif
