using Microsoft.Web.WebView2.WinForms;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using Weavie.Win.Hosting;
using Weavie.Win.Terminal;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Win;

/// <summary>
/// One workspace's host window: a thin WinForms shell over <see cref="HostCore"/>. It owns only the native
/// pieces — the frameless WebView2 host, the custom chrome (caption strip + resize edges), window geometry,
/// the Debug Vite dev server, and screenshots — and exposes them to the shared core through
/// <see cref="IHostPlatform"/> (in WorkspaceWindow.Platform.cs). It also implements <see cref="IShellWindow"/>
/// so the web title bar's window controls drive it. Everything else (the Core graph, the session set, the
/// web-message dispatch, the IDE-MCP + LSP servers) lives in the shared core. Created/tracked by
/// <see cref="AppController"/>; the app-global stores it shares come from there.
/// </summary>
internal sealed partial class WorkspaceWindow : Form, IShellWindow, IHostPlatform {
	// Synthetic host for the virtual-host mapping; https keeps the page in a secure same-origin context
	// (workers + Event Timing API behave), mirroring the macOS app:// scheme.
	private const string AppHost = "weavie.app";

	// Maps the WKWebView script-message API the shared frontend speaks onto WebView2's postMessage,
	// so the web app runs unmodified across platforms.
	private const string BridgeShim =
		"""
        (function () {
          window.webkit = window.webkit || {};
          window.webkit.messageHandlers = window.webkit.messageHandlers || {};
          window.webkit.messageHandlers.weavie = {
            postMessage: function (body) { window.chrome.webview.postMessage(body); }
          };
        })();
        """;

	private readonly AppController _app;
	private readonly string _workspaceRoot;
	private readonly HostBridge _bridge = new();
	private readonly WindowsPtyLauncher _ptyLauncher = new();
	private readonly WebView2 _webView;
	private readonly HostCore _core;
	private readonly IUiDispatcher _dispatcher;
	private readonly WinDialogs _dialogs;
	private bool _lastMaximized;
	private bool _webViewTornDown;
	// Backs the title bar's blur dim, tracked from Activated/Deactivate.
	private bool _focused = true;
#if DEBUG
	// The shared Debug dev-server bring-up (Weavie.Hosting.Web); owns the Vite process + error page.
	private DevWebBringUp? _devBringUp;
	// The Vite dev origin the WebView is pointed at, once resolved (null in Release / when serving the bundle).
	private string? _devOrigin;
	// Set while a dev-server-loss recovery navigation is in flight (re-entrancy guard); capped attempts stop a
	// pathological revive→fail→revive loop before falling back to the bundle.
	private bool _recoveringDevServer;
	private int _devRecoveryAttempts;
#endif

	// The app's dark background, painted on every host surface before the page loads so the ~0.25s of
	// WebView2 cold-start shows dark instead of the default white.
	private static readonly Color StartupBackground = Color.FromArgb(0x00, 0x00, 0x00);

	public WorkspaceWindow(AppController app, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(app);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_app = app;
		_workspaceRoot = workspaceRoot;
		Id = WorkspaceId.ForPath(workspaceRoot);

		Text = $"{WorkspaceLabel(workspaceRoot)} — weavie";
		Icon = AppIcon.Shared;
		BackColor = StartupBackground;

		// The native pieces handed to the core through IHostPlatform: the UI-thread marshal and the dialogs.
		// Built before the core (its constructor reads the dispatcher off this platform).
		_dispatcher = new DelegateUiDispatcher(action => {
			if (InvokeRequired) {
				BeginInvoke(action);
			} else {
				action();
			}
		});
		_dialogs = new WinDialogs(this);

		// The shared core over this workspace, driven by the app-global Core stores (shared across windows).
		_core = new HostCore(this, new HostServices {
			Settings = _app.Settings,
			CommandRegistry = _app.CommandRegistry,
			Keybindings = _app.Keybindings,
			ThemeOverrides = _app.ThemeOverrides,
			ClaudeSessions = _app.ClaudeSessions,
		}, workspaceRoot);
		// On the page's `ready`, push the initial native window state (maximize glyph + blur dim) the core can't know.
		_core.Ready += OnPageReady;

		ApplySavedWindowState();
		_lastMaximized = WindowState == FormWindowState.Maximized;

		// DefaultBackgroundColor must be set before the CoreWebView2 initializes so the render surface is dark
		// from the first frame; BackColor covers the control itself before that surface exists.
		_webView = new WebView2 {
			Dock = DockStyle.Fill,
			BackColor = StartupBackground,
			DefaultBackgroundColor = StartupBackground,
		};
		Controls.Add(_webView);

		Load += OnLoad;
		ResizeEnd += (_, _) => SaveWindowState();
		SizeChanged += OnWindowSizeChanged;
		Activated += (_, _) => SetFocused(true);
		Deactivate += (_, _) => SetFocused(false);
		FormClosing += OnFormClosing;
		FormClosed += (_, _) => Shutdown();
	}

	/// <summary>This workspace's stable identity (path-derived), used by the app to dedupe/focus windows.</summary>
	public WorkspaceId Id { get; }

	/// <summary>
	/// True when this window was closed via File ▸ Close Window (<see cref="CloseToWelcome"/>) rather than the
	/// title-bar X / Alt+F4. The app uses it to decide whether closing the last window falls back to the
	/// welcome window (explicit Close Window) or quits (X).
	/// </summary>
	public bool ClosedToWelcome { get; private set; }

	/// <summary>
	/// Closes this window with the intent that, if it's the last one open, the app shows the welcome window
	/// instead of quitting. The File ▸ Close Window path; hitting the title-bar X quits the app instead.
	/// </summary>
	public void CloseToWelcome() {
		ClosedToWelcome = true;
		Close();
	}

	/// <inheritdoc/>
	protected override void OnHandleCreated(EventArgs e) {
		base.OnHandleCreated(e);
		NativeChrome.UseDarkTitleBar(Handle);
		// Frameless chrome: keep the OS drop shadow + rounded corners even though the caption is removed.
		CustomChrome.EnableShadow(Handle);
	}

	/// <summary>
	/// Routes the frameless-window messages to <see cref="CustomChrome"/>: <c>WM_NCCALCSIZE</c> strips the
	/// caption (the WebView fills the whole client area), <c>WM_NCHITTEST</c> re-supplies the edge resize
	/// zones. The styles stay <c>WS_THICKFRAME | WS_CAPTION</c>, so Aero Snap + maximize still work.
	/// </summary>
	protected override void WndProc(ref Message m) {
		// Drop bare Alt/F10 menu-bar activation: there's no native menu bar to focus (it's in the web title bar).
		if (CustomChrome.HandleSysKeyMenu(ref m)) {
			return;
		}

		// IsMaximized (live WS_MAXIMIZE), not WindowState: WinForms updates WindowState on WM_SIZE, which fires
		// after WM_NCCALCSIZE, so during a maximize WindowState would still read Normal here.
		bool maximized = CustomChrome.IsMaximized(Handle);
		if (CustomChrome.HandleNcCalcSize(ref m, maximized) || CustomChrome.HandleNcHitTest(Handle, ref m, maximized)) {
			return;
		}

		base.WndProc(ref m);
	}

	/// <summary>Applies the persisted window geometry, or a centered default when there's none or it's off-screen.</summary>
	private void ApplySavedWindowState() {
		var saved = _core.SavedWindow;
		if (saved is not null) {
			var bounds = new Rectangle(saved.X, saved.Y, saved.Width, saved.Height);
			if (IsOnScreen(bounds)) {
				StartPosition = FormStartPosition.Manual;
				Bounds = bounds;
				if (saved.Maximized) {
					WindowState = FormWindowState.Maximized;
				}

				return;
			}
		}

		ClientSize = new Size(1280, 840);
		StartPosition = FormStartPosition.CenterScreen;
	}

	/// <summary>Persists maximize/restore transitions; drag resize/move is covered by <c>ResizeEnd</c>.</summary>
	private void OnWindowSizeChanged(object? sender, EventArgs e) {
		bool maximized = WindowState == FormWindowState.Maximized;
		if (WindowState != FormWindowState.Minimized && maximized != _lastMaximized) {
			_lastMaximized = maximized;
			SaveWindowState();
			// Keep the title bar's maximize/restore glyph in sync with the actual window state.
			PushWindowState();
		}
	}

	/// <summary>Updates focus state (for the title bar's blur dim) and re-pushes the window state.</summary>
	private void SetFocused(bool focused) {
		_focused = focused;
		PushWindowState();
	}

	private void OnPageReady() => PushWindowState();

	/// <summary>Pushes the current maximized + focused state to the title bar.</summary>
	private void PushWindowState() =>
		_core.PushWindowState(WindowState == FormWindowState.Maximized, _focused);

	// IShellWindow — the platform primitives the web title bar drives (Weavie.Core.Shell). Close() (the ✕) is
	// satisfied implicitly by Form.Close(); CloseWindow() (File ▸ Close Window) routes through CloseToWelcome().
	void IShellWindow.Minimize() => WindowState = FormWindowState.Minimized;

	void IShellWindow.ToggleMaximize() =>
		WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

	void IShellWindow.StartResize(ResizeEdge edge) {
		if (WindowState == FormWindowState.Maximized) {
			return;
		}

		CustomChrome.StartResize(Handle, edge);
	}

	void IShellWindow.CloseWindow() => CloseToWelcome();

	void IShellWindow.Quit() => _app.Quit();

	void IShellWindow.ShowOpenFolderPicker() => _app.OpenFolderInteractive(this);

	void IShellWindow.OpenWorkspace(string path) => _app.OpenOrFocus(path);

	private void SaveWindowState() {
		if (WindowState == FormWindowState.Minimized) {
			return;
		}

		_core.SaveWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, using the restore (un-maximized) bounds when maximized.</summary>
	private LayoutGeometry CaptureWindowState() {
		var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
		return new LayoutGeometry {
			X = bounds.X,
			Y = bounds.Y,
			Width = bounds.Width,
			Height = bounds.Height,
			Maximized = WindowState == FormWindowState.Maximized,
		};
	}

	private static bool IsOnScreen(Rectangle bounds) =>
		bounds is { Width: > 0, Height: > 0 }
		&& Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));

	/// <summary>The folder's leaf name for the window title (e.g. <c>weavie</c> for <c>C:\src\weavie</c>).</summary>
	private static string WorkspaceLabel(string root) {
		string leaf = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return string.IsNullOrEmpty(leaf) ? root : leaf;
	}

	/// <summary>
	/// Runs as the window closes — before the handle is destroyed and while the message pump is still alive.
	/// Persists geometry, then tears the WebView2 down deterministically (the automatic control disposal runs
	/// only after <see cref="Shutdown"/>, racing WebView2's native teardown and leaving the process alive).
	/// </summary>
	private void OnFormClosing(object? sender, FormClosingEventArgs e) {
		SaveWindowState();
		if (_webViewTornDown) {
			return; // FormClosing can fire more than once; dispose the view exactly once.
		}

		_webViewTornDown = true;
		try {
			_webView.Dispose();
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] webview teardown: {ex.Message}");
		}
	}

	private void Shutdown() {
		_core.Ready -= OnPageReady;
		// Tears down the sessions (terminals / IDE-MCP / LSP) and detaches the core's handlers from the
		// app-global stores. The stores themselves are owned by AppController and not disposed here.
		_core.DisposeAsync().AsTask().GetAwaiter().GetResult();
#if DEBUG
		_devBringUp?.Dispose();
#endif
	}
}
