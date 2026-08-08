using System.Runtime.InteropServices;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Sessions;
using Weavie.Core.Shell;
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
internal sealed partial class WorkspaceHost : IWebSurface, IShellMenuActions {
	// The default welcome-window size before a workspace (with its saved geometry) is opened.
	private const int WelcomeWidth = 1000;
	private const int WelcomeHeight = 680;

	private readonly HostBridge _bridge = new();

	private HostCore? _core;
	private HostServices? _services;
	private ApplicationHotkeys? _hotkeys;
	private RecentWorkspaces? _recents;
	private LinuxNotificationService? _notifications;
	private SystemNotificationChannel? _notificationChannel;
	private AppSchemeHandler? _scheme;
	private string? _wwwroot;

	private IntPtr _window;
	private IntPtr _webView;
	private IntPtr _contentManager;
	private bool _shown;
	// Kept alive: native holds a bare function pointer to this.
	private WidgetCallback? _onDestroy;
	private KeyEventCallback? _onKeyPress;
	private PropertyNotifyCallback? _onWindowStateChanged;

	/// <summary>
	/// Builds the window, view, scheme handler, and bridge, then opens the resolved workspace or — when there is
	/// none — the welcome screen. Must run on the GTK main thread (after <c>gtk_init</c>, before <c>gtk_main</c>).
	/// </summary>
	internal void Start() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		_wwwroot = wwwroot;

		// App-global Core stores + the recents that drive reopen-last and the welcome screen's list.
		_services = HostServices.CreateDefault();
		_recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		_notifications = new LinuxNotificationService(Log);

		_scheme = new AppSchemeHandler(wwwroot);
		_scheme.Register(WebKit.webkit_web_context_get_default());
		_contentManager = WebKit.webkit_user_content_manager_new();
		_bridge.RegisterOn(_contentManager);
		_webView = WebKit.webkit_web_view_new_with_user_content_manager(_contentManager);
		_bridge.Attach(_webView);
		_onKeyPress = OnKeyPress;
		_ = GLib.g_signal_connect_data(
			_webView, "key-press-event", Marshal.GetFunctionPointerForDelegate(_onKeyPress), IntPtr.Zero, IntPtr.Zero, 0);
		WebKit.webkit_settings_set_enable_developer_extras(WebKit.webkit_web_view_get_settings(_webView), true);

		_window = Gtk.gtk_window_new(Gtk.WindowToplevel);
		Gtk.gtk_window_set_title(_window, "weavie");
		IntPtr icon = GdkPixbuf.LoadFile(Path.Combine(AppContext.BaseDirectory, "weavie.png"));
		Gtk.gtk_window_set_icon(_window, icon);
		GLib.g_object_unref(icon);
		Gtk.gtk_container_add(_window, _webView);
		_onDestroy = OnWindowDestroy;
		_ = GLib.g_signal_connect_data(
			_window, "destroy", Marshal.GetFunctionPointerForDelegate(_onDestroy), IntPtr.Zero, IntPtr.Zero, 0);
		_onWindowStateChanged = OnWindowStateChanged;
		_ = GLib.g_signal_connect_data(
			_window, "notify::is-active", Marshal.GetFunctionPointerForDelegate(_onWindowStateChanged), IntPtr.Zero, IntPtr.Zero, 0);
		_ = GLib.g_signal_connect_data(
			_window, "notify::is-maximized", Marshal.GetFunctionPointerForDelegate(_onWindowStateChanged), IntPtr.Zero, IntPtr.Zero, 0);
		_hotkeys = new ApplicationHotkeys(
			_services.CommandRegistry,
			_services.Keybindings,
			new LinuxGlobalHotkeys(ApplyWaylandActivationToken),
			ToggleWindow,
			Log);

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
		_notificationChannel = _notifications!.CreateChannel();
		_core = new HostCore(
			new LinuxPlatform(_bridge, _recents, this, _notificationChannel, ToggleWindow, ActivateWindow),
			_services!,
			root,
			WorkspaceHttpServerOptions.Native(_wwwroot!),
			UnavailableWorkspaceWebSocketBridge.Instance);
		_core.Ready += PushWindowState;

		// Linux can't enumerate monitor work-areas (no GDK binding), so the on-screen guard is inert and saved
		// bounds are trusted; the empty screen list leaves it that way.
		var placement = WindowPlacement.Resolve(_core.SavedWindow, [], 1280, 840);
		ApplyGeometry(placement);

		// Synchronous before gtk_main (or on the main loop when opened from welcome): StartAsync does I/O (git) but
		// touches nothing GTK-affine.
		_core.StartAsync().GetAwaiter().GetResult();

		// Drop any welcome injection (its window.__WEAVIE_WELCOME__) so it can't leak into the workspace page.
		// The shared server injects the workspace bootstrap into index.html before the module graph.
		WebKit.webkit_user_content_manager_remove_all_scripts(_contentManager);

		ShowWindow();
		WebKit.webkit_web_view_load_uri(_webView, _core.WorkspaceNativePageUrl);
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
		DisposeHotkeys();
		CloseWorkspace();
		_notifications?.Dispose();
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	private void OnWindowDestroy(IntPtr widget, IntPtr userData) {
		SaveWindowState();
		Gtk.gtk_main_quit();
	}

	private void ToggleWindow() {
		if (IsWindowActive()) {
			Gtk.gtk_widget_hide(_window);
			return;
		}

		Gtk.gtk_widget_show_all(_window);
		Gtk.gtk_window_present(_window);
		_shown = true;
	}

	private bool IsWindowActive() {
		IntPtr display = Gdk.gdk_display_get_default();
		if (Gdk.GetDisplayBackend(display) != Gdk.DisplayBackend.X11) {
			return Gtk.gtk_window_is_active(_window);
		}

		IntPtr active = Gdk.gdk_screen_get_active_window(Gdk.gdk_screen_get_default());
		if (active == IntPtr.Zero) {
			return Gtk.gtk_window_is_active(_window);
		}
		try {
			return Gdk.gdk_x11_window_get_xid(active)
				== Gdk.gdk_x11_window_get_xid(Gtk.gtk_widget_get_window(_window));
		} finally {
			GLib.g_object_unref(active);
		}
	}

	private void ApplyWaylandActivationToken(string token) {
		if (!Gtk.gtk_window_is_active(_window)) {
			Gdk.gdk_wayland_display_set_startup_notification_id(Gdk.gdk_display_get_default(), token);
		}
	}

	private void ActivateWindow(string? activationToken) {
		if (!string.IsNullOrEmpty(activationToken)) {
			ApplyWaylandActivationToken(activationToken);
		}
		Gtk.gtk_widget_show_all(_window);
		Gtk.gtk_window_present(_window);
		_shown = true;
	}

	private void OnWindowStateChanged(IntPtr instance, IntPtr property, IntPtr userData) =>
		PushWindowState();

	private void PushWindowState() =>
		_core?.PushWindowState(Gtk.gtk_window_is_maximized(_window), IsWindowActive());

	private void DisposeHotkeys() {
		_hotkeys?.Dispose();
		_hotkeys = null;
	}

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
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
