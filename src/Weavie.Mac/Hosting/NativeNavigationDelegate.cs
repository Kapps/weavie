using Weavie.Hosting;
using WebKit;

namespace Weavie.Mac.Hosting;

internal class NativeNavigationDelegate(NativeBridgeSecurity security) : WKNavigationDelegate {
	public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decide) =>
		decide(action.TargetFrame is { } frame && security.Allows(action.Request.Url?.AbsoluteString, frame.MainFrame)
			? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);

	public override void DecidePolicy(WKWebView webView, WKNavigationResponse response, Action<WKNavigationResponsePolicy> decide) =>
		decide(security.Allows(response.Response.Url?.AbsoluteString, response.IsForMainFrame)
			? WKNavigationResponsePolicy.Allow : WKNavigationResponsePolicy.Cancel);
}
