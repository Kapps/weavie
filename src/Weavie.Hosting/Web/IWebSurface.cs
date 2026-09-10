namespace Weavie.Hosting.Web;

/// <summary>
/// The few native WebView operations the shared web bring-up needs, the seam that lets the dev-server +
/// bootstrap + navigation flow live once in <see cref="WebAppLauncher"/>/<c>DevWebBringUp</c>. WebView2 (Win),
/// WKWebView (Mac), WebKitGTK (Linux) each implement these; each marshals onto its own UI thread.
/// </summary>
public interface IWebSurface {
	/// <summary>Installs the authenticated bridge and startup data before loading the selected app document.</summary>
	Task LoadAsync(string url, string startupScript);

	/// <summary>Revokes the bridge and displays an unprivileged recovery document.</summary>
	void RenderHtml(string html);
}
