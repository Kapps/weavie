namespace Weavie.Hosting.Web;

/// <summary>
/// The few genuinely native WebView operations the shared web bring-up needs from a host shell — the seam
/// that lets the dev-server + bootstrap + navigation flow live once in <see cref="WebAppLauncher"/> /
/// <see cref="DevWebBringUp"/> instead of being reimplemented per OS. WebView2 (Win), WKWebView (Mac) and
/// WebKitGTK (Linux) each implement these three; every impl is responsible for marshaling onto its own UI
/// thread, so the shared flow stays thread-agnostic.
/// </summary>
public interface IWebSurface {
	/// <summary>Navigates the WebView to <paramref name="url"/> (the chosen origin's <c>/index.html</c>, or a recovery target).</summary>
	void Navigate(string url);

	/// <summary>Loads an HTML document string directly into the WebView (no network, no backend) — used for the dev-server error page.</summary>
	void RenderHtml(string html);

	/// <summary>Registers a script to run at document start on the next navigation (the bootstrap globals). Completes once registered.</summary>
	Task InjectStartupScriptAsync(string script);
}
