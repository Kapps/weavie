using WebKit;

namespace Weavie.Mac.Hosting;

/// <summary>
/// The workspace webview's window.open / target=_blank policy: an in-app child window is never created (it
/// would share the bridge); a web URL opens in the OS browser instead — the Win host's
/// <c>NewWindowRequested</c> analogue.
/// </summary>
internal sealed class WorkspaceUiDelegate : WKUIDelegate {
	private readonly Action<string> _openExternal;

	public WorkspaceUiDelegate(Action<string> openExternal) {
		ArgumentNullException.ThrowIfNull(openExternal);
		_openExternal = openExternal;
	}

	public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures) {
		string url = navigationAction.Request?.Url?.AbsoluteString ?? string.Empty;
		if (Uri.TryCreate(url, UriKind.Absolute, out var parsed)
			&& (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)) {
			_openExternal(parsed.AbsoluteUri);
		}

		return null;
	}
}
