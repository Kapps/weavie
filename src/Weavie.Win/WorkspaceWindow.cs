using Microsoft.Web.WebView2.WinForms;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Hosting.Web;
using Weavie.Win.Hosting;
using LayoutGeometry = Weavie.Core.Layout.WindowState;
using PixelRect = Weavie.Core.Layout.PixelRect;
using WindowPlacement = Weavie.Core.Layout.WindowPlacement;

namespace Weavie.Win;

/// <summary>
/// One workspace's host window: a thin WinForms shell over <see cref="HostCore"/>. Owns only the native pieces
/// (the frameless WebView2 host, custom chrome, geometry, the Debug Vite dev server, screenshots), exposed through
/// <see cref="IHostPlatform"/>, and implements <see cref="IShellWindow"/> so the web title bar drives it. Everything
/// else lives in the shared core. Created/tracked by <see cref="AppController"/>, which owns the shared stores.
/// </summary>
internal sealed partial class WorkspaceWindow : Form, IShellWindow, IHostPlatform {
	// Synthetic virtual-host name; https keeps the page in a secure same-origin context (workers + Event Timing
	// API behave), mirroring the macOS app:// scheme.
	private const string AppHost = "weavie.app";

	// Maps the WKWebView script-message API the shared frontend speaks onto WebView2's postMessage, so the web app
	// runs unmodified across platforms.
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
	// The Vite dev origin the WebView is pointed at, once resolved (null when serving the bundle).
	private string? _devOrigin;
	// Re-entrancy guard while a dev-server-loss recovery navigation is in flight; capped attempts stop a
	// revive→fail→revive loop before falling back to the bundle.
	private bool _recoveringDevServer;
	private int _devRecoveryAttempts;
#endif

	// Painted on every host surface before the page loads, so the WebView2 cold-start shows dark, not white.
	private static readonly Color StartupBackground = Color.FromArgb(0x00, 0x00, 0x00);

	public WorkspaceWindow(AppController app, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(app);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_app = app;
		_workspaceRoot = workspaceRoot;
		Id = WorkspaceId.ForPath(workspaceRoot);

		Text = $"{WorkspaceNaming.Label(workspaceRoot)} — weavie";
		Icon = AppIcon.Shared;
		BackColor = StartupBackground;

		// Native pieces handed to the core via IHostPlatform; built before the core, whose constructor reads the
		// dispatcher off this platform.
		_dispatcher = new DelegateUiDispatcher(action => {
			if (InvokeRequired) {
				BeginInvoke(action);
			} else {
				action();
			}
		});
		_dialogs = new WinDialogs(this);

		// The shared core over this workspace, driven by the app-global Core stores (shared across windows). One
		// GitHub client backs both PR listing and review comments.
		var github = new Weavie.Core.Review.GitHubReviewProvider(http: null, new Weavie.Core.Review.GitHubTokenSource());
		_core = new HostCore(this, new HostServices {
			Settings = _app.Settings,
			CommandRegistry = _app.CommandRegistry,
			Keybindings = _app.Keybindings,
			ThemeOverrides = _app.ThemeOverrides,
			ClaudeSessions = _app.ClaudeSessions,
			RemoteAgents = _app.RemoteAgents,
			RailState = _app.RailState,
			PullRequests = github,
			ReviewComments = github,
		}, workspaceRoot);
		// On the page's `ready`, push the native window state (maximize glyph + blur dim) the core can't know.
		_core.Ready += OnPageReady;

		ApplySavedWindowState();
		_lastMaximized = WindowState == FormWindowState.Maximized;

		// DefaultBackgroundColor must be set before CoreWebView2 initializes so the render surface is dark from the
		// first frame; BackColor covers the control before that surface exists.
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
	/// True when closed via File ▸ Close Window (<see cref="CloseToWelcome"/>) rather than the title-bar X. Lets the
	/// app show the welcome window when the last window closes this way, or quit when it closes via X.
	/// </summary>
	public bool ClosedToWelcome { get; private set; }

	/// <summary>
	/// Closes this window so that, if it's the last open, the app shows the welcome window instead of quitting
	/// (the File ▸ Close Window path).
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
	/// Routes frameless-window messages to <see cref="CustomChrome"/>: <c>WM_NCCALCSIZE</c> strips the caption,
	/// <c>WM_NCHITTEST</c> re-supplies the edge resize zones. Styles stay <c>WS_THICKFRAME | WS_CAPTION</c>, so Aero
	/// Snap + maximize still work.
	/// </summary>
	protected override void WndProc(ref Message m) {
		// Drop bare Alt/F10 menu-bar activation: there's no native menu bar to focus (it's in the web title bar).
		if (CustomChrome.HandleSysKeyMenu(ref m)) {
			return;
		}

		// IsMaximized (live WS_MAXIMIZE), not WindowState, which WinForms updates only on WM_SIZE (after NCCALCSIZE).
		bool maximized = CustomChrome.IsMaximized(Handle);
		if (CustomChrome.HandleNcCalcSize(ref m, maximized) || CustomChrome.HandleNcHitTest(Handle, ref m, maximized)) {
			return;
		}

		base.WndProc(ref m);
	}

	/// <summary>Applies the persisted window geometry, or a centered default when there's none or it's off-screen.</summary>
	private void ApplySavedWindowState() {
		var screens = Screen.AllScreens
			.Select(s => new PixelRect(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height))
			.ToList();
		var placement = WindowPlacement.Resolve(_core.SavedWindow, screens, 1280, 840);
		if (placement.UseSaved) {
			StartPosition = FormStartPosition.Manual;
			Bounds = new Rectangle(placement.X, placement.Y, placement.Width, placement.Height);
			if (placement.Maximized) {
				WindowState = FormWindowState.Maximized;
			}
		} else {
			ClientSize = new Size(placement.Width, placement.Height);
			StartPosition = FormStartPosition.CenterScreen;
		}
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
	// satisfied by Form.Close(); CloseWindow() (File ▸ Close Window) routes through CloseToWelcome().
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

	/// <summary>
	/// Persists geometry as the window closes, then tears the WebView2 down deterministically — automatic control
	/// disposal races WebView2's native teardown and can leave the process alive.
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
		// app-global stores. The stores themselves are owned by AppController.
		_core.DisposeAsync().AsTask().GetAwaiter().GetResult();
#if DEBUG
		_devBringUp?.Dispose();
#endif
	}
}
