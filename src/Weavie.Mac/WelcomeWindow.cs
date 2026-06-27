using CoreGraphics;
using Foundation;
using Weavie.Hosting.Web;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

/// <summary>
/// The app's empty state on macOS: an <see cref="NSWindow"/> rendering welcome.html in a <see cref="WKWebView"/>,
/// with no workspace or core until a folder is opened. The shared <see cref="WelcomeController"/> injects the
/// recents and routes the page's Open Folder / Open Recent through the controller's open logic; opening a folder
/// dismisses it (see <c>AppDelegate</c>), and closing it with no workspace window open lets the app terminate.
/// </summary>
internal sealed class WelcomeWindow : IWebSurface {
	private const int DefaultWidth = 920;
	private const int DefaultHeight = 640;

	private readonly AppDelegate _app;
	private readonly HostBridge _bridge = new();
	private readonly WKWebView _webView;
	private readonly WelcomeController _controller;
	private NSObject? _closeObserver;

	/// <summary>Builds the welcome window over the bundled welcome.html, with the current recents, and shows it.</summary>
	public WelcomeWindow(AppDelegate app) {
		ArgumentNullException.ThrowIfNull(app);
		_app = app;

		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
#if DEBUG
		// Allow the Web Inspector for local debugging (Debug builds only).
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));
#endif

		var frame = new CGRect(0, 0, DefaultWidth, DefaultHeight);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);
		_controller = new WelcomeController(
			_bridge, this, "app://app/welcome.html", () => _app.Recents.Items, _app.OpenFolderInteractive, _app.OpenOrFocus);

		Window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = "weavie",
			ContentView = _webView,
		};
		Window.Center();
		_closeObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => OnClosed(), Window);
		Window.MakeKeyAndOrderFront(null);
		NSApplication.SharedApplication.Activate();

		_ = _controller.ShowAsync();
	}

	/// <summary>The native window, so the controller can focus or dismiss it.</summary>
	public NSWindow Window { get; }

	/// <summary>Re-injects the current recents and reloads welcome.html so a pruned entry drops out of the list.</summary>
	internal void RefreshRecents() {
		_ = _controller.RefreshAsync();
	}

	// IWebSurface — the WKWebView ops the shared welcome flow drives; each marshals onto the main thread.
	void IWebSurface.Navigate(string url) =>
		_app.Dispatcher.Post(() => _webView.LoadRequest(new NSUrlRequest(new NSUrl(url))));

	void IWebSurface.RenderHtml(string html) =>
		_app.Dispatcher.Post(() => _webView.LoadHtmlString(new NSString(html), null));

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_app.Dispatcher.Post(() => {
			try {
				_webView.Configuration.UserContentController.AddUserScript(new WKUserScript(
					new NSString(script), WKUserScriptInjectionTime.AtDocumentStart, isForMainFrameOnly: true));
				tcs.SetResult();
			} catch (Exception ex) {
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}

	private void OnClosed() {
		_controller.Detach();
		if (_closeObserver is not null) {
			NSNotificationCenter.DefaultCenter.RemoveObserver(_closeObserver);
			_closeObserver = null;
		}

		_webView.Configuration.UserContentController.RemoveScriptMessageHandler("weavie");
		_app.OnWelcomeClosed();
	}
}
