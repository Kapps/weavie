using System.Text;
using System.Text.Json;
using CoreGraphics;
using Foundation;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

/// <summary>
/// The app's empty state on macOS: an <see cref="NSWindow"/> rendering welcome.html in a <see cref="WKWebView"/>,
/// with no workspace or core until a folder is opened. It drives the app via the same <c>menu-action</c> bridge
/// messages the File menu uses (Open Folder / Open Recent); opening a folder dismisses it (see <c>AppDelegate</c>),
/// and closing it with no workspace window open lets the app terminate.
/// </summary>
internal sealed class WelcomeWindow {
	private const int DefaultWidth = 920;
	private const int DefaultHeight = 640;

	private readonly AppDelegate _app;
	private readonly HostBridge _bridge = new();
	private readonly WKWebView _webView;
	private NSObject? _closeObserver;

	/// <summary>Builds the welcome window over the bundled welcome.html, with the current recents injected, and shows it.</summary>
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
		_bridge.MessageReceived += OnWebMessage;

		// Recents reach the page as window.__WEAVIE_WELCOME__, injected before navigation (no flash, no round-trip).
		InjectRecents();

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

		// Always the bundled wwwroot; the empty state never probes for a Vite dev server.
		_webView.LoadRequest(new NSUrlRequest(new NSUrl("app://app/welcome.html")));
	}

	/// <summary>The native window, so the controller can focus or dismiss it.</summary>
	public NSWindow Window { get; }

	/// <summary>Re-injects the current recents and reloads welcome.html so a pruned entry drops out of the list.</summary>
	internal void RefreshRecents() {
		InjectRecents();
		_webView.LoadRequest(new NSUrlRequest(new NSUrl("app://app/welcome.html")));
	}

	private void InjectRecents() =>
		_webView.Configuration.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_WELCOME__ = {BuildWelcomeJson(_app.Recents.Items)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

	// Routes the welcome screen's Open Folder / Open Recent to the app's open logic; other messages no-op.
	private void OnWebMessage(string json) {
		string action;
		string? path;
		try {
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			if (!root.TryGetProperty("type", out var type) || type.GetString() != "menu-action") {
				return;
			}

			action = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
			path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
		} catch (JsonException) {
			return;
		}

		switch (action) {
			case "open-folder":
				_app.OpenFolderInteractive();
				break;
			case "open-recent":
				if (!string.IsNullOrEmpty(path)) {
					_app.OpenOrFocus(path);
				}

				break;
		}
	}

	private void OnClosed() {
		if (_closeObserver is not null) {
			NSNotificationCenter.DefaultCenter.RemoveObserver(_closeObserver);
			_closeObserver = null;
		}

		_webView.Configuration.UserContentController.RemoveScriptMessageHandler("weavie");
		_bridge.MessageReceived -= OnWebMessage;
		_app.OnWelcomeClosed();
	}

	// Built by hand: JsonSerializer.Serialize is trim-unsafe (IL2026) on macOS.
	private static string BuildWelcomeJson(IReadOnlyList<string> recents) {
		var sb = new StringBuilder("{\"recents\":[");
		for (int i = 0; i < recents.Count; i++) {
			if (i > 0) {
				sb.Append(',');
			}

			sb.Append('"').Append(JsonEncodedText.Encode(recents[i]).ToString()).Append('"');
		}

		return sb.Append("]}").ToString();
	}
}
