namespace Weavie.Hosting.Web;

/// <summary>
/// The few native WebView operations the shared web bring-up needs, the seam that lets the dev-server +
/// bootstrap + navigation flow live once in <see cref="WebAppLauncher"/>/<c>DevWebBringUp</c>. WebView2 (Win),
/// WKWebView (Mac), WebKitGTK (Linux) each implement these; each marshals onto its own UI thread.
/// </summary>
public interface IWebSurface {
	/// <summary>Navigates the WebView to <paramref name="url"/> (the chosen origin's <c>/index.html</c>, or a recovery target).</summary>
	void Navigate(string url);

	/// <summary>Loads an HTML document string directly into the WebView (no network, no backend) — used for the dev-server error page.</summary>
	void RenderHtml(string html);

	/// <summary>Registers a script to run at document start on the next navigation (the bootstrap globals). Completes once registered.</summary>
	Task InjectStartupScriptAsync(string script);
}
