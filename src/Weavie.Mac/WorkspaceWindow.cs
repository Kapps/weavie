using CoreGraphics;
using Foundation;
using Weavie.Core.Layout;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

/// <summary>
/// One workspace window: a native <see cref="NSWindow"/> + <see cref="WKWebView"/> over its own <see cref="HostCore"/>,
/// the macOS counterpart of the Windows <c>WorkspaceWindow</c>. The app delegate owns a set of these (one per open
/// folder) so a second folder opens a real window the user switches between with normal macOS window switching, not a
/// relaunch. App-global stores (settings, keybindings, recents, hotkeys, dialogs) are shared in from the controller.
/// </summary>
internal sealed partial class WorkspaceWindow : IWebSurface {
	private readonly AppDelegate _app;
	private readonly HostBridge _bridge = new();
	private readonly HostCore _core;
	private readonly WKWebView _webView;
	private NSObject? _resizeObserver;
	private NSObject? _closeObserver;
	private NSObject? _keyObserver;
	private bool _disposed;

	/// <summary>Opens a window onto <paramref name="workspace"/>, restoring its saved geometry, and starts the web app.</summary>
	public WorkspaceWindow(AppDelegate app, string workspace) {
		ArgumentNullException.ThrowIfNull(app);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		_app = app;

		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
#if DEBUG
		// Allow the Web Inspector for local debugging (Debug builds only).
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));
#endif
		// Render at the display's full refresh (120Hz) instead of WKWebView's default 60fps pacing.
		WebKitFeatureFlags.DisablePrefer60Fps(config.Preferences);

		_core = new HostCore(
			this,
			app.Services,
			workspace,
			WorkspaceHttpServerOptions.Native(wwwroot),
			UnavailableWorkspaceWebSocketBridge.Instance);

		// Startup geometry via the shared placement policy: saved-if-valid-and-on-screen, else centered.
		var screens = NSScreen.Screens
			.Select(s => new PixelRect((int)s.VisibleFrame.X, (int)s.VisibleFrame.Y, (int)s.VisibleFrame.Width, (int)s.VisibleFrame.Height))
			.ToList();
		var placement = WindowPlacement.Resolve(_core.SavedWindow, screens, 1280, 840);
		var frame = new CGRect(placement.X, placement.Y, placement.Width, placement.Height);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);
		// Navigation policy: Release cancels any main-frame navigation away from the app origin and reroutes it
		// to the OS browser; Debug intercepts the dev error page's weavie-dev:// links and allows the dev origin.
#if DEBUG
		_webView.NavigationDelegate = new WorkspaceNavigationDelegate(
			onRetry: () => _ = RetryDevServerAsync(),
			onLoadBundle: () => _ = LoadBundleAsync());
#else
		_webView.NavigationDelegate = new WorkspaceNavigationDelegate(
			() => _core.WorkspaceOrigin,
			url => ((IHostPlatform)this).OpenExternalUrl(url));
#endif
		// window.open / target=_blank never creates an in-app window — web URLs open in the OS browser.
		_webView.UIDelegate = new WorkspaceUiDelegate(url => ((IHostPlatform)this).OpenExternalUrl(url));

		Window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = $"{WorkspaceNaming.Label(workspace)} — weavie",
			ContentView = _webView,
		};
		if (!placement.UseSaved) {
			Window.Center();
		} else if (placement.Maximized) {
			Window.Zoom(null);
		}

		// Persist geometry on resize-end and on close; SaveWindow no-ops when nothing changed. Closing also tears the
		// window's core down and drops it from the controller. Becoming key marks it the global-hotkey toggle target.
		_resizeObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidEndLiveResizeNotification, _ => SaveWindowState(), Window);
		_closeObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => _app.OnWindowClosed(this), Window);
		_keyObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidBecomeKeyNotification, _ => _app.MarkActive(this), Window);
		Window.MakeKeyAndOrderFront(null);

		// Fire-and-forget so the readiness poll doesn't block the open.
		_ = LoadWebAppAsync();
	}

	/// <summary>This window's workspace identity (path-derived), so the controller can focus an already-open folder.</summary>
	public WorkspaceId Id => _core.Id;

	/// <summary>The native window, so the controller can focus/raise it.</summary>
	public NSWindow Window { get; }

	/// <summary>Runs a Weavie command id against this window's core (a menu item targeting the front window).</summary>
	public void InvokeCommand(string id) => _core.InvokeCommand(id);

	/// <summary>Shows a transient notification in this window's page (e.g. a folder-not-found error).</summary>
	public void Notify(string level, string message) => _core.Notify(level, message);

	/// <summary>Persists this window's geometry; no-op while miniaturized (a minimized frame isn't worth restoring).</summary>
	public void SaveWindowState() {
		if (Window.IsMiniaturized) {
			return;
		}

		_core.SaveWindow(CaptureWindowState());
	}

	/// <summary>Tears the window's core down (terminals / IDE-MCP / LSP) and removes its notification observers.</summary>
	public void DisposeCore() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		if (_resizeObserver is not null) {
			NSNotificationCenter.DefaultCenter.RemoveObserver(_resizeObserver);
			_resizeObserver = null;
		}

		if (_closeObserver is not null) {
			NSNotificationCenter.DefaultCenter.RemoveObserver(_closeObserver);
			_closeObserver = null;
		}

		if (_keyObserver is not null) {
			NSNotificationCenter.DefaultCenter.RemoveObserver(_keyObserver);
			_keyObserver = null;
		}

		_core.DisposeAsync().AsTask().GetAwaiter().GetResult();
#if DEBUG
		_devBringUp?.Dispose(); // kills the Vite dev server this window spawned; a reused one is left alone
#endif
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-zoomed restore bounds while zoomed.</summary>
	private WindowState CaptureWindowState() {
		if (Window.IsZoomed && _core.SavedWindow is { } prior) {
			return prior with { Maximized = true };
		}

		var frame = Window.Frame;
		return new WindowState {
			X = (int)frame.X,
			Y = (int)frame.Y,
			Width = (int)frame.Width,
			Height = (int)frame.Height,
			Maximized = Window.IsZoomed,
		};
	}
}
