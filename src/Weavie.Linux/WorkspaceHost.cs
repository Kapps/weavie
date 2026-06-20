using System.Runtime.InteropServices;
using Weavie.Hosting;
using Weavie.Linux.Hosting;
using Weavie.Linux.Native;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Linux;

/// <summary>
/// The GTK + WebKitGTK application host: a thin shell over <see cref="HostCore"/>. It owns only the native
/// window, the WebKitGTK view + <c>app://</c> scheme handler, the GLib-main-loop bridge, and window geometry;
/// everything else (the Core graph, the session set, the web-message dispatch) lives in the shared core. The
/// Linux counterpart of the macOS / Windows shells.
/// </summary>
internal sealed class WorkspaceHost {
	private readonly HostBridge _bridge = new();

	private HostCore? _core;
	private HostServices? _services;
	private AppSchemeHandler? _scheme;

	private IntPtr _window;
	private IntPtr _webView;
	private IntPtr _contentManager;
	// Kept alive for the lifetime of the host: native holds a bare function pointer to this.
	private WidgetCallback? _onDestroy;

	/// <summary>
	/// Builds the window and WebKit view, registers the <c>app://</c> scheme handler and the <c>weavie</c>
	/// script-message bridge, builds the shared core (terminals + IDE-MCP + LSP + sessions), injects the
	/// bootstrap globals, restores the saved geometry, and loads the web app. Must be called on the GTK main
	/// thread (after <c>gtk_init</c>, before <c>gtk_main</c>).
	/// </summary>
	internal void Start() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

		// App-global Core stores + the workspace this host serves.
		_services = HostServices.CreateDefault();
		string workspace = _services.Settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_core = new HostCore(new LinuxPlatform(_bridge), _services, workspace);

		// Restore the saved window geometry when present and sane, else a 1280x840 default.
		var savedWindow = _core.SavedWindow;
		bool usedSaved = savedWindow is { Width: > 0, Height: > 0 };
		int width = usedSaved ? savedWindow!.Width : 1280;
		int height = usedSaved ? savedWindow!.Height : 840;

		// Custom app:// scheme on the default web context, the script-message bridge on a fresh user-content
		// manager, then the view bound to that manager.
		_scheme = new AppSchemeHandler(wwwroot);
		_scheme.Register(WebKit.webkit_web_context_get_default());
		_contentManager = WebKit.webkit_user_content_manager_new();
		_bridge.RegisterOn(_contentManager);
		_webView = WebKit.webkit_web_view_new_with_user_content_manager(_contentManager);
		_bridge.Attach(_webView);
		WebKit.webkit_settings_set_enable_developer_extras(WebKit.webkit_web_view_get_settings(_webView), true);

		// Build the live backend (sessions / IDE-MCP / LSP) and wire the bridge. Synchronous on the GTK main
		// thread at startup, before gtk_main: StartAsync does I/O (git) but touches nothing GTK-affine, and
		// the bridge marshals its own posts onto the main loop.
		_core.StartAsync("app://app").GetAwaiter().GetResult();

		// Inject the bootstrap globals (fonts / editor / theme / lsp / commands / keybindings / shell) before
		// navigation so both surfaces mount at the user's settings with no flash.
		InjectAtDocumentStart(_core.BuildBootstrap());

		// Window: a single top-level holding the web view, restored to the saved geometry.
		_window = Gtk.gtk_window_new(Gtk.WindowToplevel);
		Gtk.gtk_window_set_title(_window, "weavie");
		Gtk.gtk_window_set_default_size(_window, width, height);
		Gtk.gtk_container_add(_window, _webView);
		if (usedSaved) {
			Gtk.gtk_window_move(_window, savedWindow!.X, savedWindow.Y);
			if (savedWindow.Maximized) {
				Gtk.gtk_window_maximize(_window);
			}
		}

		_onDestroy = OnWindowDestroy;
		_ = GLib.g_signal_connect_data(
			_window, "destroy", Marshal.GetFunctionPointerForDelegate(_onDestroy), IntPtr.Zero, IntPtr.Zero, 0);

		Gtk.gtk_widget_show_all(_window);
		WebKit.webkit_web_view_load_uri(_webView, "app://app/index.html");
	}

	/// <summary>Persists geometry, tears down the core (terminals + IDE-MCP), and disposes the app stores; called after the main loop exits.</summary>
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
