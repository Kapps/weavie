using WebKit;
#if DEBUG
using Weavie.Hosting.Web;
#endif

namespace Weavie.Mac.Hosting;

/// <summary>
/// The workspace webview's navigation policy: the view only ever shows Weavie. A Release main-frame navigation
/// to any web origin other than the app's own is cancelled and routed to the OS browser (the Win host's
/// <c>NavigationStarting</c> analogue); non-web schemes (about:, data:) and subframe navigations (the web-tab
/// iframe browsing) pass through. Debug builds instead intercept the dev-server error page's
/// <c>weavie-dev://</c> action links and allow everything else — the dev bundle runs off a different origin,
/// so origin enforcement is Release-only, matching the Win host.
/// </summary>
internal sealed class WorkspaceNavigationDelegate : WKNavigationDelegate {
#if DEBUG
	private readonly Action _onRetry;
	private readonly Action _onLoadBundle;

	public WorkspaceNavigationDelegate(Action onRetry, Action onLoadBundle) {
		ArgumentNullException.ThrowIfNull(onRetry);
		ArgumentNullException.ThrowIfNull(onLoadBundle);
		_onRetry = onRetry;
		_onLoadBundle = onLoadBundle;
	}
#else
	private readonly Func<string> _workspaceOrigin;
	private readonly Action<string> _openExternal;

	public WorkspaceNavigationDelegate(Func<string> workspaceOrigin, Action<string> openExternal) {
		ArgumentNullException.ThrowIfNull(workspaceOrigin);
		ArgumentNullException.ThrowIfNull(openExternal);
		_workspaceOrigin = workspaceOrigin;
		_openExternal = openExternal;
	}
#endif

	public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler) {
		string url = navigationAction.Request?.Url?.AbsoluteString ?? string.Empty;
#if DEBUG
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
#else
		if (navigationAction.TargetFrame is not { MainFrame: true } || !IsForeignWebUrl(url)) {
			decisionHandler(WKNavigationActionPolicy.Allow);
			return;
		}

		decisionHandler(WKNavigationActionPolicy.Cancel);
		_openExternal(url);
#endif
	}

#if !DEBUG
	// A web URL outside the app's own origin — the only navigations the policy refuses (and reroutes).
	private bool IsForeignWebUrl(string url) {
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
			return false;
		}

		return !(Uri.TryCreate(_workspaceOrigin(), UriKind.Absolute, out var workspace)
			&& uri.Scheme == workspace.Scheme && uri.Host == workspace.Host && uri.Port == workspace.Port);
	}
#endif
}
