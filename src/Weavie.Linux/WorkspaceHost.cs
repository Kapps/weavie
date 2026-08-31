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
/// GTK 4 + WebKitGTK host: a thin shell over <see cref="HostCore"/> owning only the native window, web view,
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
	// Kept alive: native holds a bare function pointer to this.
	private WidgetCallback? _onDestroy;
	private KeyPressedCallback? _onKeyPress;
	private PropertyNotifyCallback? _onWindowStateChanged;

	/// <summary>
	/// Builds the window, view, scheme handler, and bridge, then opens the resolved workspace or — when there is
	/// none — the welcome screen. Must run on the GTK main thread (after <c>gtk_init</c>, before the main loop).
	/// </summary>
	internal void Start() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		_wwwroot = wwwroot;
		CreateNativeWindow();
		ShowWindow();

		// App-global Core stores + the recents that drive reopen-last and the welcome screen's list.
		_services = HostServices.CreateDefault();
		_recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		_notifications = new LinuxNotificationService(Log);

		_scheme = new AppSchemeHandler(wwwroot);
		_scheme.Register(WebKit.webkit_web_context_get_default());
		_webView = WebKit.webkit_web_view_new();
		_contentManager = WebKit.webkit_web_view_get_user_content_manager(_webView);
		_bridge.RegisterOn(_contentManager);
		_bridge.Attach(_webView);
		AttachKeyController();
		IntPtr settings = WebKit.webkit_web_view_get_settings(_webView);
		WebKit.webkit_settings_set_enable_developer_extras(settings, true);
		WebKit.EnableNativeRefreshRate(settings);

		Gtk.gtk_window_set_child(_window, _webView);
		if (!Gtk.gtk_widget_grab_focus(_webView)) {
			throw new InvalidOperationException("The Linux web view could not take keyboard focus.");
		}
		_hotkeys = new ApplicationHotkeys(
			_services.CommandRegistry,
			_services.Keybindings,
			new LinuxGlobalHotkeys(ApplyActivationToken),
			ToggleWindow,
			Log);

		StartInstanceServer();
		// A path the OS handed us decides the workspace; only without one does reopen-last apply.
		string? workspace = LaunchWorkspace() ?? InitialWorkspace.Resolve(_services.Settings, _recents);
		if (workspace is null) {
			ShowWelcome();
		} else {
			OpenWorkspace(workspace);
			OpenLaunchPaths();
		}
	}

	private void CreateNativeWindow() {
		_window = Gtk.gtk_window_new();
		Gtk.gtk_window_set_title(_window, "weavie");
		Gtk.gtk_window_set_default_size(_window, WelcomeWidth, WelcomeHeight);
		Gtk.gtk_window_set_icon_name(_window, LinuxDesktopIdentity.AppId);
		_onDestroy = OnWindowDestroy;
		_ = GLib.g_signal_connect_data(
			_window, "destroy", Marshal.GetFunctionPointerForDelegate(_onDestroy), IntPtr.Zero, IntPtr.Zero, 0);
		_onWindowStateChanged = OnWindowStateChanged;
		_ = GLib.g_signal_connect_data(
			_window, "notify::is-active", Marshal.GetFunctionPointerForDelegate(_onWindowStateChanged), IntPtr.Zero, IntPtr.Zero, 0);
		_ = GLib.g_signal_connect_data(
			_window, "notify::maximized", Marshal.GetFunctionPointerForDelegate(_onWindowStateChanged), IntPtr.Zero, IntPtr.Zero, 0);
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
		// bounds are trusted; the empty screen list leaves it that way. Only the size and maximized state are
		// applied — GTK 4 has no client-side window positioning on either backend.
		var placement = WindowPlacement.Resolve(_core.SavedWindow, [], 1280, 840);
		ApplyGeometry(placement);

		// Synchronous before the main loop starts (or on it when opened from welcome): StartAsync does I/O (git) but
		// touches nothing GTK-affine.
		_core.StartAsync().GetAwaiter().GetResult();

		// Drop any welcome injection (its window.__WEAVIE_WELCOME__) so it can't leak into the workspace page.
		// The shared server injects the workspace bootstrap into index.html before the module graph.
		WebKit.webkit_user_content_manager_remove_all_scripts(_contentManager);

		ShowWindow();
		WebKit.webkit_web_view_load_uri(_webView, _core.WorkspaceNativePageUrl);
	}

	/// <summary>Sizes the window for <paramref name="placement"/>, live when it is already on screen (welcome → workspace).</summary>
	private void ApplyGeometry(StartupPlacement placement) {
		Gtk.gtk_window_set_default_size(_window, placement.Width, placement.Height);
		if (placement is { UseSaved: true, Maximized: true }) {
			Gtk.gtk_window_maximize(_window);
		}
	}

	private void ShowWindow() => Gtk.gtk_window_present(_window);

	/// <summary>Persists geometry, tears down the core, and disposes the app stores; called after the main loop exits.</summary>
	internal void Shutdown() {
		StopInstanceServer();
		DisposeHotkeys();
		CloseWorkspace();
		_notifications?.Dispose();
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	private void OnWindowDestroy(IntPtr widget, IntPtr userData) {
		SaveWindowState();
		GtkMain.Quit();
	}

	private void ToggleWindow() {
		if (IsWindowActive()) {
			Gtk.gtk_widget_set_visible(_window, false);
			return;
		}

		ShowWindow();
	}

	private bool IsWindowActive() => Gtk.gtk_window_is_active(_window);

	private void ApplyActivationToken(string token) {
		if (!Gtk.gtk_window_is_active(_window)) {
			Gtk.gtk_window_set_startup_id(_window, token);
		}
	}

	private void ActivateWindow(string? activationToken) {
		if (!string.IsNullOrEmpty(activationToken)) {
			ApplyActivationToken(activationToken);
		}
		ShowWindow();
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

	/// <summary>
	/// Snapshots the current geometry. A maximized or hidden window is not reporting the bounds to restore to
	/// — hidden it reports nothing at all — so the last ones it did report stand.
	/// </summary>
	private LayoutGeometry CaptureWindowState() {
		bool maximized = Gtk.gtk_window_is_maximized(_window);
		int width = Gtk.gtk_widget_get_width(_window);
		int height = Gtk.gtk_widget_get_height(_window);
		if ((maximized || width == 0 || height == 0) && _core!.SavedWindow is { } prior) {
			return prior with { Maximized = maximized };
		}

		return new LayoutGeometry {
			X = 0,
			Y = 0,
			Width = width,
			Height = height,
			Maximized = maximized,
		};
	}
}
