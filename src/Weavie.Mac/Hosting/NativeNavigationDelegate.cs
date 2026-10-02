using Weavie.Hosting;
using WebKit;

namespace Weavie.Mac.Hosting;

internal class NativeNavigationDelegate(NativeBridgeSecurity security) : WKNavigationDelegate {
	public override void DecidePolicy(WKWebView webView, WKNavigationAction action, Action<WKNavigationActionPolicy> decide) {
		bool allowed = action.TargetFrame is { } frame && security.Allows(action.Request.Url?.AbsoluteString, frame.MainFrame);
		decide(allowed ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
		if (!allowed) NativeBridgeSecurity.ReportBlockedNavigation(action.TargetFrame?.MainFrame == true);
	}

	public override void DecidePolicy(WKWebView webView, WKNavigationResponse response, Action<WKNavigationResponsePolicy> decide) {
		bool allowed = security.Allows(response.Response.Url?.AbsoluteString, response.IsForMainFrame);
		decide(allowed ? WKNavigationResponsePolicy.Allow : WKNavigationResponsePolicy.Cancel);
		if (!allowed) NativeBridgeSecurity.ReportBlockedNavigation(response.IsForMainFrame);
	}
}
