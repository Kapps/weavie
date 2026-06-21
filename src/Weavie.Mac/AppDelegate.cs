using System.Reflection;
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
/// The macOS application delegate: a thin shell over <see cref="HostCore"/>. Owns only the native pieces —
/// the WKWebView host window + <c>app://</c> scheme, the NSMenu, Carbon global hotkeys, native file dialogs,
/// the main-thread marshal, and window geometry/screenshots — exposed to the shared core through
/// <see cref="IHostPlatform"/> (in AppDelegate.Platform.cs). Everything else lives in the shared core.
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
	// Shared dev-server bring-up; compiled out in Release.
	private DevWebBringUp? _devBringUp;
#endif

	/// <summary>
	/// Creates the host window and WKWebView, registers the <c>app://</c> scheme handler and <c>weavie</c>
	/// script-message bridge, builds the shared core, native menu, and global hotkeys, then loads the web app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
		// Allow the Web Inspector for local debugging.
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));
		// Render at the display's full refresh (120Hz) instead of WKWebView's default 60fps pacing.
		WebKitFeatureFlags.DisablePrefer60Fps(config.Preferences);

		// App-global Core stores + the single workspace this process serves. The recents list surfaces in
		// File ▸ Open Recent + the omnibar shell config.
		_services = HostServices.CreateDefault();
		var (workspace, recents) = WorkspaceBootstrap.Resolve(_services.Settings);
		_workspace = workspace;
		_recents = recents;

		// Native capabilities handed to the core through IHostPlatform: the UI-thread marshal, global hotkeys,
		// and file dialogs. Created before the core (its constructor reads the dispatcher).
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

		// Startup geometry via the shared placement policy (saved-if-valid-and-on-screen, else centered).
		var screens = NSScreen.Screens
			.Select(s => new PixelRect((int)s.VisibleFrame.X, (int)s.VisibleFrame.Y, (int)s.VisibleFrame.Width, (int)s.VisibleFrame.Height))
			.ToList();
		var placement = WindowPlacement.Resolve(_core.SavedWindow, screens, 1280, 840);
		var frame = new CGRect(placement.X, placement.Y, placement.Width, placement.Height);
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
			Title = $"{WorkspaceNaming.Label(workspace)} — weavie",
			ContentView = _webView,
		};
		if (!placement.UseSaved) {
			_window.Center();
		} else if (placement.Maximized) {
			_window.Zoom(null);
		}

		// Persist geometry on resize-end and on close; SaveWindow no-ops when nothing changed.
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidEndLiveResizeNotification, _ => SaveWindowState(), _window);
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => SaveWindowState(), _window);
		_window.MakeKeyAndOrderFront(null);

		nint screenHz = (_window.Screen ?? NSScreen.MainScreen)?.MaximumFramesPerSecond ?? 0;
		Console.WriteLine($"[weavie] NSScreen.maximumFramesPerSecond = {screenHz}");

		// Native menu bar: File/View menus plus the standard App/Edit/Window menus. File/View items dispatch the
		// same Weavie command ids the keyboard + omnibar use; their shortcuts are read from the keybinding store.
		NSApplication.SharedApplication.MainMenu = MacAppMenu.Build(
			runCommand: id => _core?.InvokeCommand(id),
			resolveChord: ResolveChord,
			openFolder: OpenFolderInteractive,
			openRecent: SwitchWorkspace,
			recents: _recents.Items);

		// Resolve the page origin (Debug: Vite dev server; Release: bundled app:// wwwroot), build the backend,
		// inject bootstrap globals, and navigate. Fire-and-forget so the readiness poll doesn't block launch.
		_ = LoadWebAppAsync();

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot: fire from the native run loop (a JS timer throttles when occluded).
		// Gated on WEAVIE_SHOT_DIR so the shipped app never writes screenshots.
		if (ScreenshotRequest.FromEnvironment() is { } shot) {
			NSTimer.CreateScheduledTimer(shot.DelaySeconds, repeats: false, _ => CaptureSnapshot(shot));
		}
	}

	/// <summary>
	/// Brings the app up via the shared <see cref="WebAppLauncher"/> — backend, bootstrap injection, navigation.
	/// In Debug, <c>DevWebBringUp</c> tries the Vite dev server and renders a loud error page on failure (never a
	/// silent stale-bundle swap); Release loads the bundled <c>app://</c> assets.
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
	// thread (WebKit is main-thread-affine), so the shared flow stays thread-agnostic.
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
	/// <summary>Retry button on the dev-server error page: ask the shared bring-up to try again.</summary>
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
		_core?.DisposeAsync().AsTask().GetAwaiter().GetResult(); // disposes sessions + global hotkeys
#if DEBUG
		_devBringUp?.Dispose(); // kills the Vite dev server this run spawned; a reused one is left alone
#endif
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	/// <summary>
	/// Toggles Weavie — behind the global hotkey and <c>weavie.window.toggle</c>. When active, hide it (focus
	/// returns to the previous app); otherwise activate + raise it. Must run on the main thread.
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


	private void CaptureSnapshot(ScreenshotRequest shot) {
		if (_webView is null) {
			return;
		}

		Directory.CreateDirectory(shot.DirectoryPath);
		string path = shot.TargetPath;

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
