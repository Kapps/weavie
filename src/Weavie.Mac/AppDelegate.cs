using System.Reflection;
using CoreGraphics;
using Foundation;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

/// <summary>
/// The macOS application delegate: a thin shell over <see cref="HostCore"/>. It owns only the native pieces —
/// the WKWebView host window + <c>app://</c> scheme, the NSMenu, Carbon global hotkeys, native file dialogs,
/// the main-thread marshal, and window geometry/screenshots — and exposes them to the shared core through
/// <see cref="IHostPlatform"/> (implemented in AppDelegate.Platform.cs). Everything else (the Core graph, the
/// session set, the web-message dispatch, the IDE-MCP + LSP servers) lives in the shared core.
/// </summary>
[Register("AppDelegate")]
public sealed partial class AppDelegate : NSApplicationDelegate, IWebSurface {
	private readonly HostBridge _bridge = new();
	private readonly PosixPtyLauncher _ptyLauncher = new();
	private HostCore? _core;
	private HostServices? _services;
	private RecentWorkspaces? _recents;
	private MacGlobalHotkeys? _hotkeyRegistrar;
	private MacDialogs? _dialogs;
	private IUiDispatcher? _dispatcher;
	private string? _workspace;
	private NSWindow? _window;
	private WKWebView? _webView;
#if DEBUG
	// Debug-only shared dev-server bring-up (Weavie.Hosting.Web); null in Release (the types are compiled out there).
	private DevWebBringUp? _devBringUp;
#endif

	/// <summary>
	/// Creates the host window and WKWebView, registers the <c>app://</c> scheme handler and <c>weavie</c>
	/// script-message bridge, builds the shared core, the native menu, and the global hotkeys, then resolves the
	/// page origin and loads the web app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
		// Allow the Web Inspector for local debugging of the prototype.
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));
		// Render at the display's full refresh (120Hz) instead of WKWebView's default 60fps pacing.
		WebKitFeatureFlags.DisablePrefer60Fps(config.Preferences);

		// App-global Core stores + the single workspace this process serves.
		_services = HostServices.CreateDefault();
		string workspace = _services.Settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_workspace = workspace;
		// Recents: record this workspace and surface the list in File ▸ Open Recent + the omnibar shell config.
		_recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		_recents.Add(workspace);

		// Native capabilities handed to the core through IHostPlatform: the UI-thread marshal, global hotkeys,
		// and the file dialogs. Created before the core (its constructor reads the dispatcher).
		_dispatcher = new DelegateUiDispatcher(action => {
			if (NSThread.IsMain) {
				action();
			} else {
				NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
			}
		});
		_hotkeyRegistrar = new MacGlobalHotkeys();
		_hotkeyRegistrar.Log += Log;
		_dialogs = new MacDialogs();

		_core = new HostCore(this, _services, workspace);

		// Restore the saved window geometry when present and on-screen, else a centered 1280x840 default.
		var savedWindow = _core.SavedWindow;
		bool usedSaved = savedWindow is not null
			&& IsOnScreen(new CGRect(savedWindow.X, savedWindow.Y, savedWindow.Width, savedWindow.Height));
		var frame = usedSaved
			? new CGRect(savedWindow!.X, savedWindow.Y, savedWindow.Width, savedWindow.Height)
			: new CGRect(0, 0, 1280, 840);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);
#if DEBUG
		// Intercept the dev-server error page's weavie-dev:// action links (Retry / Load stale bundle).
		_webView.NavigationDelegate = new DevLinkNavigationDelegate(
			onRetry: () => _ = RetryDevServerAsync(),
			onLoadBundle: () => _ = LoadBundleAsync());
#endif

		_window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = $"{WorkspaceLabel(workspace)} — weavie",
			ContentView = _webView,
		};
		if (!usedSaved) {
			_window.Center();
		} else if (savedWindow is { Maximized: true }) {
			_window.Zoom(null);
		}

		// Persist geometry on resize-end and on close; SaveWindow no-ops when nothing actually changed.
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidEndLiveResizeNotification, _ => SaveWindowState(), _window);
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => SaveWindowState(), _window);
		_window.MakeKeyAndOrderFront(null);

		nint screenHz = (_window.Screen ?? NSScreen.MainScreen)?.MaximumFramesPerSecond ?? 0;
		Console.WriteLine($"[weavie] NSScreen.maximumFramesPerSecond = {screenHz}");

		// Native menu bar: the macOS counterpart of the web title bar's File/View menus, plus the standard
		// App/Edit/Window menus. File/View items dispatch the same Weavie command ids the keyboard + omnibar use
		// (run on the active session via the core); their shortcuts are read from the keybinding store.
		NSApplication.SharedApplication.MainMenu = MacAppMenu.Build(
			runCommand: id => _core?.InvokeCommand(id),
			resolveChord: ResolveChord,
			openFolder: OpenFolderInteractive,
			openRecent: SwitchWorkspace,
			recents: _recents.Items);

		// Resolve the page origin (Debug: the Vite dev server; Release: the bundled app:// wwwroot), build the
		// live backend, inject the bootstrap globals, and navigate. Fire-and-forget so the dev-server readiness
		// poll doesn't block launch; the load is marshaled back to the main thread.
		_ = LoadWebAppAsync();

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot for the deliverable: fire from the native run loop (not a JS timer, which
		// throttles when occluded). Gated on WEAVIE_SHOT_DIR so the shipped app never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
			NSTimer.CreateScheduledTimer(delay, repeats: false, _ => CaptureSnapshot());
		}
	}

	/// <summary>
	/// Brings the app up via the shared <see cref="WebAppLauncher"/> — backend, bootstrap injection, navigation.
	/// In Debug, <see cref="DevWebBringUp"/> first tries the Vite dev server and renders a loud error page on
	/// failure (never a silent stale-bundle swap); Release loads the bundled <c>app://</c> assets. Fire-and-forget
	/// so the dev-server readiness poll doesn't block launch; the surface marshals the UI work onto the main thread.
	/// </summary>
	private async Task LoadWebAppAsync() {
		if (_core is null) {
			return;
		}

		var launcher = new WebAppLauncher(this, _core, string.Empty);
#if DEBUG
		_devBringUp = new DevWebBringUp(
			launcher, this,
			DevWebRoot.Resolve(Assembly.GetExecutingAssembly()),
			"app://app",
			line => {
				Console.WriteLine($"[vite] {line}");
				Console.Out.Flush();
			});
		await _devBringUp.RunAsync().ConfigureAwait(false);
#else
		await launcher.LaunchAsync("app://app").ConfigureAwait(false);
#endif
	}

	// IWebSurface — the native WKWebView operations the shared web bring-up drives. Each marshals onto the main
	// thread (WebKit is main-thread-affine), so the shared flow in Weavie.Hosting.Web stays thread-agnostic.
	void IWebSurface.Navigate(string url) =>
		_dispatcher!.Post(() => _webView?.LoadRequest(new NSUrlRequest(new NSUrl(url))));

	void IWebSurface.RenderHtml(string html) =>
		_dispatcher!.Post(() => _webView?.LoadHtmlString(new NSString(html), null));

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_dispatcher!.Post(() => {
			try {
				_webView?.Configuration.UserContentController.AddUserScript(new WKUserScript(
					new NSString(script),
					WKUserScriptInjectionTime.AtDocumentStart,
					isForMainFrameOnly: true));
				tcs.SetResult();
			} catch (Exception ex) {
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}

#if DEBUG
	/// <summary>Retry button on the dev-server error page: ask the shared bring-up to try again (it launches on
	/// success, re-renders the error page on failure).</summary>
	private async Task RetryDevServerAsync() {
		if (_devBringUp is null) {
			return;
		}

		Console.WriteLine("[weavie] retrying Vite dev server…");
		await _devBringUp.RunAsync().ConfigureAwait(false);
	}

	/// <summary>"Load stale bundle anyway" button: the developer explicitly accepts the possibly-stale bundle.</summary>
	private async Task LoadBundleAsync() {
		if (_devBringUp is null) {
			return;
		}

		Console.WriteLine("[weavie] loading STALE bundled wwwroot at app://app (explicit developer choice)");
		await _devBringUp.LoadBundleAsync().ConfigureAwait(false);
	}
#endif

	/// <summary>Quits the app when its last (only) window is closed.</summary>
	public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

	/// <summary>Persists geometry, tears down the core (terminals / IDE-MCP / LSP / hotkeys), and disposes the app stores on exit.</summary>
	public override void WillTerminate(NSNotification notification) {
		SaveWindowState();
		_core?.DisposeAsync().AsTask().GetAwaiter().GetResult(); // disposes sessions + the global hotkeys
#if DEBUG
		_devBringUp?.Dispose(); // kills the Vite dev server this run spawned (a reused one is left alone)
#endif
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	/// <summary>
	/// Toggles Weavie — the handler behind the global hotkey and <c>weavie.window.toggle</c>. When the app is
	/// active, hide it (focus returns to the previous app); otherwise activate + raise it. Must run on the main thread.
	/// </summary>
	private void ToggleWindow() {
		var app = NSApplication.SharedApplication;
		if (app.Active) {
			app.Hide(this);
		} else {
			app.Activate();
			_window?.MakeKeyAndOrderFront(null);
		}
	}

	private void SaveWindowState() {
		if (_window is null || _core is null || _window.IsMiniaturized) {
			return;
		}

		_core.SaveWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-zoomed restore bounds while zoomed.</summary>
	private Core.Layout.WindowState CaptureWindowState() {
		if (_window!.IsZoomed && _core!.SavedWindow is { } prior) {
			return prior with { Maximized = true };
		}

		var frame = _window.Frame;
		return new Core.Layout.WindowState {
			X = (int)frame.X,
			Y = (int)frame.Y,
			Width = (int)frame.Width,
			Height = (int)frame.Height,
			Maximized = _window.IsZoomed,
		};
	}

	private static bool IsOnScreen(CGRect frame) =>
		frame.Width > 0 && frame.Height > 0
		&& NSScreen.Screens.Any(screen => screen.VisibleFrame.IntersectsWith(frame));

	private void CaptureSnapshot() {
		string? dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		if (_webView is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		string? name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		string path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

		_webView.TakeSnapshot(new WKSnapshotConfiguration(), (image, error) => {
			if (image is null) {
				Console.Error.WriteLine($"[weavie] snapshot failed: {error?.LocalizedDescription}");
				return;
			}

			var tiff = image.AsTiff();
			if (tiff is null) {
				return;
			}

			using var rep = new NSBitmapImageRep(tiff);
			var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
			if (png is null) {
				Console.Error.WriteLine("[weavie] snapshot: PNG encoding failed");
				return;
			}

			if (png.Save(path, true, out var saveError)) {
				Console.WriteLine($"[weavie] snapshot saved: {path}");
			} else {
				Console.Error.WriteLine($"[weavie] snapshot save failed: {saveError?.LocalizedDescription}");
			}

			Console.Out.Flush();
		});
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
