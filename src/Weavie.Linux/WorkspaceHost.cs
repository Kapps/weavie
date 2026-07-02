using System.Runtime.InteropServices;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Linux;

/// <summary>
/// GTK + WebKitGTK host: a thin shell over <see cref="HostCore"/> owning only the native window, web view,
/// <c>app://</c> scheme, main-loop bridge, and geometry; the rest lives in the shared core. Launch reopens the
/// last workspace (else the <c>workspace</c> setting); with neither, it shows the welcome screen
/// (<c>WorkspaceHost.Welcome.cs</c>) until the user opens a folder.
/// </summary>
internal sealed partial class WorkspaceHost : IWebSurface {
	// The default welcome-window size before a workspace (with its saved geometry) is opened.
	private const int WelcomeWidth = 1000;
	private const int WelcomeHeight = 680;

	private readonly HostBridge _bridge = new();

	private HostCore? _core;
	private HostServices? _services;
	private RecentWorkspaces? _recents;
	private AppSchemeHandler? _scheme;

	private IntPtr _window;
	private IntPtr _webView;
	private IntPtr _contentManager;
	private bool _shown;
	// Kept alive: native holds a bare function pointer to this.
	private WidgetCallback? _onDestroy;

	/// <summary>
	/// Builds the window, view, scheme handler, and bridge, then opens the resolved workspace or — when there is
	/// none — the welcome screen. Must run on the GTK main thread (after <c>gtk_init</c>, before <c>gtk_main</c>).
	/// </summary>
	internal void Start() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

		// App-global Core stores + the recents that drive reopen-last and the welcome screen's list.
		_services = HostServices.CreateDefault();
		_recents = new RecentWorkspaces(new LocalFileSystem(), path: null);

		_scheme = new AppSchemeHandler(wwwroot);
		_scheme.Register(WebKit.webkit_web_context_get_default());
		_contentManager = WebKit.webkit_user_content_manager_new();
		_bridge.RegisterOn(_contentManager);
		_webView = WebKit.webkit_web_view_new_with_user_content_manager(_contentManager);
		_bridge.Attach(_webView);
		WebKit.webkit_settings_set_enable_developer_extras(WebKit.webkit_web_view_get_settings(_webView), true);

		_window = Gtk.gtk_window_new(Gtk.WindowToplevel);
		Gtk.gtk_window_set_title(_window, "weavie");
		Gtk.gtk_container_add(_window, _webView);
		_onDestroy = OnWindowDestroy;
		_ = GLib.g_signal_connect_data(
			_window, "destroy", Marshal.GetFunctionPointerForDelegate(_onDestroy), IntPtr.Zero, IntPtr.Zero, 0);

		string? workspace = InitialWorkspace.Resolve(_services.Settings, _recents);
		if (workspace is null) {
			ShowWelcome();
		} else {
			OpenWorkspace(workspace);
		}
	}

	/// <summary>
	/// Brings up the live workspace at <paramref name="root"/>: records it in recents, builds the core, restores
	/// the window geometry, injects the bootstrap, and loads the app. Called at launch or from the welcome screen.
	/// </summary>
	private void OpenWorkspace(string root) {
		_recents!.Add(root);
		_core = new HostCore(new LinuxPlatform(_bridge, _recents), _services!, root);

		// Linux can't enumerate monitor work-areas (no GDK binding), so the on-screen guard is inert and saved
		// bounds are trusted; the empty screen list leaves it that way.
		var placement = WindowPlacement.Resolve(_core.SavedWindow, [], 1280, 840);
		ApplyGeometry(placement);

		// Synchronous before gtk_main (or on the main loop when opened from welcome): StartAsync does I/O (git) but
		// touches nothing GTK-affine.
		_core.StartAsync().GetAwaiter().GetResult();

		// Drop any welcome injection (its window.__WEAVIE_WELCOME__) so it can't leak into the workspace page, then
		// inject the bootstrap globals before navigation so the app mounts at the user's settings with no flash.
		WebKit.webkit_user_content_manager_remove_all_scripts(_contentManager);
		InjectAtDocumentStart(_core.BuildBootstrap());

		ShowWindow();
		WebKit.webkit_web_view_load_uri(_webView, "app://app/index.html");
	}

	/// <summary>Sizes/positions the window for <paramref name="placement"/>; resizes live when already on screen (welcome → workspace).</summary>
	private void ApplyGeometry(StartupPlacement placement) {
		if (_shown) {
			Gtk.gtk_window_resize(_window, placement.Width, placement.Height);
		} else {
			Gtk.gtk_window_set_default_size(_window, placement.Width, placement.Height);
		}

		if (placement.UseSaved) {
			Gtk.gtk_window_move(_window, placement.X, placement.Y);
			if (placement.Maximized) {
				Gtk.gtk_window_maximize(_window);
			}
		}
	}

	private void ShowWindow() {
		Gtk.gtk_widget_show_all(_window);
		_shown = true;
	}

	/// <summary>Persists geometry, tears down the core, and disposes the app stores; called after the main loop exits.</summary>
	internal void Shutdown() {
		SaveWindowState();
		_core?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	private void OnWindowDestroy(IntPtr widget, IntPtr userData) {
		SaveWindowState();
		Gtk.gtk_main_quit();
	}

	private void InjectAtDocumentStart(string source) {
		IntPtr script = WebKit.webkit_user_script_new(
			source, WebKit.InjectTopFrame, WebKit.InjectAtDocumentStart, IntPtr.Zero, IntPtr.Zero);
		WebKit.webkit_user_content_manager_add_script(_contentManager, script);
	}

	// IWebSurface — the WelcomeController drives the welcome page through these. Every caller (Start + the bridge's
	// main-thread message handler) is already on the GTK main thread, so these touch the view directly.
	void IWebSurface.Navigate(string url) => WebKit.webkit_web_view_load_uri(_webView, url);

	void IWebSurface.RenderHtml(string html) => WebKit.webkit_web_view_load_html(_webView, html, IntPtr.Zero);

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		InjectAtDocumentStart(script);
		return Task.CompletedTask;
	}

	private void SaveWindowState() {
		if (_window == IntPtr.Zero || _core is null) {
			return;
		}

		_core.SaveWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-maximized restore bounds while maximized.</summary>
	private LayoutGeometry CaptureWindowState() {
		if (Gtk.gtk_window_is_maximized(_window) && _core!.SavedWindow is { } prior) {
			return prior with { Maximized = true };
		}

		Gtk.gtk_window_get_size(_window, out int width, out int height);
		Gtk.gtk_window_get_position(_window, out int x, out int y);
		return new LayoutGeometry {
			X = x,
			Y = y,
			Width = width,
			Height = height,
			Maximized = false,
		};
	}
}
