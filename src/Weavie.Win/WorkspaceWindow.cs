using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Layout;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Win.Hosting;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Win;

/// <summary>
/// One workspace's host window: a single full-bleed WebView2 rendering the shared Vite web app, wired to
/// the Core seams through one <see cref="HostSession"/> — a real ConPTY terminal running <c>claude</c>, a
/// shell, the loopback IDE-MCP server + lock file, the LSP bridge, and Monaco diff/file presentation. The
/// window owns the per-workspace pane layout + window geometry; the session owns the live backend. v1
/// hosts exactly one session; multiple sessions per window come later. Windows analogue of the macOS
/// per-window wiring in <c>AppDelegate</c>; created and tracked by <see cref="AppController"/>.
/// </summary>
internal sealed class WorkspaceWindow : Form, IShellWindow {
	// Synthetic host for the virtual-host mapping; https keeps the page in a secure same-origin
	// context (workers + Event Timing API behave), mirroring the macOS app:// scheme.
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

	// Perf instrumentation (the live latency HUD and its per-tick log spam) is opt-in via
	// WEAVIE_DEBUG_PERFORMANCE: surfaced to the web app as ?debugperf, and used here to gate the
	// latency-live/benchmark-result console logging. Off by default so normal runs stay quiet.
	private readonly bool _debugPerformance =
		!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_PERFORMANCE"));

	private readonly AppController _app;
	private readonly string _workspaceRoot;
	private readonly SettingsStore _settings;
	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
	private readonly LayoutStore _layout;
	private bool _lastMaximized;
	private bool _webViewTornDown;
	private HostSession? _session;
	// Drives the custom title bar: parses its window-control/menu-action/file-index messages (built once
	// the session exists). _focused backs the title bar's blur dim, tracked from Activated/Deactivate.
	private ShellController? _shell;
	private bool _focused = true;
#if DEBUG
	private WebDevServer? _webDev;
#endif

	// The app's dark background, painted on every host surface before the page loads so the ~0.25s of
	// WebView2 cold-start shows dark instead of the default white. Matches the web splash + styles --bg;
	// when theme persistence lands the host can swap in the user's resolved theme background here.
	private static readonly Color StartupBackground = Color.FromArgb(0x1e, 0x1e, 0x1e);

	public WorkspaceWindow(AppController app, string workspaceRoot) {
		ArgumentNullException.ThrowIfNull(app);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		_app = app;
		_workspaceRoot = workspaceRoot;
		_settings = app.Settings;
		Id = WorkspaceId.ForPath(workspaceRoot);

		Text = $"{WorkspaceLabel(workspaceRoot)} — weavie";
		Icon = AppIcon.Shared;
		BackColor = StartupBackground;

		// Per-workspace layout: each opened folder gets its own pane tree + window geometry under
		// ~/.weavie/workspaces/<id>/layout.json, keyed by the folder's path. Restore its saved geometry
		// (size / position / maximized) before the handle is created; a missing or off-screen saved state
		// falls back to a centered 1280x840 default.
		_layout = LayoutPanes.CreateStore(WeaviePaths.WorkspaceLayoutFile(Id));
		ApplySavedWindowState();
		_lastMaximized = WindowState == FormWindowState.Maximized;

		// DefaultBackgroundColor must be set before the CoreWebView2 initializes — it's stored and applied
		// during init, so the render surface is dark from the first frame. Setting it post-init (as before)
		// leaves the surface its default white through the ~0.2s cold-start. BackColor covers the control
		// itself in the window before that surface exists.
		_webView = new WebView2 {
			Dock = DockStyle.Fill,
			BackColor = StartupBackground,
			DefaultBackgroundColor = StartupBackground,
		};
		Controls.Add(_webView);

		// No native menu bar: the window is frameless (see WndProc/CustomChrome) and the WebView fills the
		// whole client area. The File/View menus, app icon, omnibar, and min/maximize/close all live in the
		// web title bar, which drives this window back through the IShellWindow members below.

		Load += OnLoad;
		ResizeEnd += (_, _) => SaveWindowState();
		SizeChanged += OnWindowSizeChanged;
		// Title-bar blur dim + focus state: track activation and re-push the window state to the page.
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
	/// zones. Everything else (and window dragging, via the web title bar's <c>app-region: drag</c>) is the
	/// default behavior. The styles stay <c>WS_THICKFRAME | WS_CAPTION</c>, so Aero Snap + maximize still work.
	/// </summary>
	protected override void WndProc(ref Message m) {
		// IsMaximized (live WS_MAXIMIZE), not WindowState: WinForms updates WindowState on WM_SIZE, which
		// fires after WM_NCCALCSIZE, so during a maximize WindowState would still read Normal here and the
		// frame inset would be skipped — clipping the top of the title bar.
		bool maximized = CustomChrome.IsMaximized(Handle);
		if (CustomChrome.HandleNcCalcSize(ref m, maximized) || CustomChrome.HandleNcHitTest(Handle, ref m, maximized)) {
			return;
		}

		base.WndProc(ref m);
	}

	/// <summary>Applies the persisted window geometry, or a centered default when there's none or it's off-screen.</summary>
	private void ApplySavedWindowState() {
		var saved = _layout.Current.Window;
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

	/// <summary>Pushes the current maximized + focused state to the title bar (no-op before the shell exists).</summary>
	private void PushWindowState() =>
		_shell?.PushWindowState(WindowState == FormWindowState.Maximized, _focused);

	// IShellWindow — the platform primitives the web title bar drives (Weavie.Core.Shell). Close() (the ✕)
	// is satisfied implicitly by Form.Close(); CloseWindow() (File ▸ Close Window) routes through
	// CloseToWelcome() so the last window falls back to the welcome screen instead of quitting.
	void IShellWindow.Minimize() => WindowState = FormWindowState.Minimized;

	void IShellWindow.ToggleMaximize() =>
		WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

	void IShellWindow.StartResize(ResizeEdge edge) {
		// A maximized window doesn't resize. The web hides its grab handles when maximized; this guards the
		// race where a mousedown's message arrives just after a maximize.
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

		_layout.SetWindow(CaptureWindowState());
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

	private async void OnLoad(object? sender, EventArgs e) {
		try {
			await InitializeAsync();
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] initialization failed: {ex}");
			MessageBox.Show(this, ex.ToString(), "weavie failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	private async Task InitializeAsync() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		// SetVirtualHostNameToFolderMapping throws if the folder is absent. Ensure it exists so a
		// build without web assets still opens the window (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		// WebView2 needs a writable user-data folder (the exe may live under Program Files); keep it
		// under the Weavie root so all Weavie data lives together (~/.weavie/internals/webview2).
		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		long tBeforeEnv = Program.StartupClock.ElapsedMilliseconds;
		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		long tWebViewReady = Program.StartupClock.ElapsedMilliseconds;
		var core = _webView.CoreWebView2;

		// Serve the built web app from wwwroot over https://weavie.app/ (no network, no localhost
		// port), the WebView2 counterpart of the macOS app:// scheme handler.
		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
		core.Settings.AreDevToolsEnabled = true;          // local debugging of the prototype
		core.Settings.IsStatusBarEnabled = false;
		// Let the web title bar declare its draggable caption via CSS `app-region: drag`; WebView2 then
		// handles window dragging, double-click-maximize, and the right-click system menu for the frameless
		// window. The resize edges are owned by CustomChrome (WM_NCHITTEST).
		core.Settings.IsNonClientRegionSupportEnabled = true;

		// Page origin: the shipped app loads the bundled web app over https://weavie.app/. In Debug the
		// host owns a Vite dev server — started here, torn down on exit, so there's no second terminal —
		// and points the WebView at it for hot-module reload, falling back to the bundled wwwroot if the
		// server can't start. Release loads the bundle (the block below is compiled out). The chosen
		// origin flows into navigation and the LSP bridge's allowed origin.
		string pageOrigin = $"https://{AppHost}";
#if DEBUG
		_webDev = new WebDevServer(line => {
			Console.WriteLine($"[vite] {line}");
			Console.Out.Flush();
		});
		string? devOrigin = await _webDev.StartAsync();
		if (devOrigin is not null) {
			pageOrigin = devOrigin;
			Console.WriteLine($"[weavie] hot reload: serving web from {devOrigin} (Vite dev server)");
		} else {
			Console.WriteLine("[weavie] dev server unavailable; falling back to bundled wwwroot");
		}
#endif

		_bridge.Attach(_webView);

		// Off-by-default diagnostic (a real setting, not a buried env var): when on, the host logs its
		// launch phases below and tells the web app to log its own via ?startuptiming.
		bool startupTiming = _settings.GetBool("diagnostics.startupTiming");
		_bridge.MessageReceived += OnWebMessage;

		// Session: the live, workspace-scoped backend this window hosts — the two terminals (claude +
		// shell), the IDE-MCP server + lock file (so the spawned claude connects to us as the SOLE edit
		// feed), the LSP bridge, and the file/diff presenters. Created once pageOrigin is known so the LSP
		// WS origin is pinned correctly.
		_session = new HostSession(_bridge, _settings, _layout, _workspaceRoot, pageOrigin, Guid.NewGuid().ToString("n")[..8]);

		// Custom title bar: route its window-control / menu-action / file-index messages (handled in
		// OnWebMessage) to the shared Core controller, driving this window via the IShellWindow members.
		_shell = new ShellController(this, _session.FileIndex, _bridge.PostToWeb);

		// Session changes: push the updated change list to the page whenever a tracked file changes, and a
		// targeted live-refresh of the one edited file so its open editor model updates in place (every
		// permission mode). The events fire off the UI thread (the hook accept loop); PostToWeb marshals.
		_session.Changes.Changed += PushChangesToWeb;
		_session.Changes.FileChanged += PushRefreshToWeb;

		// Inject LSP discovery (port, per-session token, workspace root) before navigation so the page can
		// lazily start a monaco-languageclient per language on first matching document.
		await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_LSP__ = {_session.LspConfigJson};");

		// Typography: inject the resolved editor + terminal fonts before navigation so both surfaces
		// mount at the user's font with no default-font flash; live changes are pushed below.
		await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings)};");

		// Custom title bar config: tell the web to render the Windows chrome (icon + File/View menus +
		// omnibar + window controls), with the workspace label and the recents for File ▸ Open Recent.
		await core.AddScriptToExecuteOnDocumentCreatedAsync(
			ShellProtocol.BuildConfigScript("win", "custom", WorkspaceLabel(_workspaceRoot), [.. _app.Recents.Items]));

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the UI thread, so marshal onto it before touching the controller.
		_settings.Subscribe("terminal.shell", _ => {
			if (InvokeRequired) {
				BeginInvoke(() => _session?.Shell.Restart());
			} else {
				_session?.Shell.Restart();
			}
		});

		// Fonts (ApplyMode.Live): any global or per-surface font change re-pushes the resolved editor +
		// terminal fonts to the web app, which applies them in place. PostToWeb marshals to the UI
		// thread itself and the store is thread-safe, so the off-thread change event can call it directly.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}
		};

		// Layout: when the store changes (a reconciled web edit, or a future MCP setLayout), push the
		// canonical document back so the web re-renders. Change events arrive off the UI thread.
		_layout.Changed += _ => {
			if (InvokeRequired) {
				BeginInvoke(PushLayoutToWeb);
			} else {
				PushLayoutToWeb();
			}
		};

		var query = new List<string>();
		if (_debugPerformance) {
			query.Add("debugperf=1");
		}
		if (startupTiming) {
			query.Add("startuptiming=1");
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_AUTOBENCH"))) {
			query.Add("autobench=1");
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_FPSPROBE"))) {
			query.Add("fpsprobe=1");
		}
		string qs = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
		core.Navigate($"{pageOrigin}/index.html{qs}");

		if (startupTiming) {
			long tNavigate = Program.StartupClock.ElapsedMilliseconds;
			Console.WriteLine(
				$"[startup] webview-ready {tWebViewReady}ms (env+ensure took {tWebViewReady - tBeforeEnv}ms), "
				+ $"host-setup {tNavigate - tWebViewReady}ms, navigate at {tNavigate}ms since process start");
			Console.Out.Flush();
		}

		// Unattended screenshot for the deliverable; gated on WEAVIE_SHOT_DIR so the shipped app
		// never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
			ScheduleOnce(delay, () => _ = CaptureSnapshotAsync());
		}

		// Dev aid: render a sample openDiff so the Monaco diff UI can be screenshotted without
		// driving claude. Gated on WEAVIE_DEMO_DIFF; never fires in normal use.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEMO_DIFF"))) {
			ScheduleOnce(2.5, PostDemoDiff);
		}
	}

	private void OnWebMessage(string json) {
		string type;
		JsonElement root;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Console.WriteLine($"[weavie] (unparsed) {json}");
			return;
		}

		switch (type) {
			case "term-input":
				byte[] input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
				if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_INPUT"))) {
					string printable = string.Concat(input.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));
					Console.WriteLine($"[weavie] term-input <- xterm ({input.Length}B): {printable}");
					Console.Out.Flush();
				}
				TerminalFor(root)?.Write(input);
				break;
			case "term-resize":
				TerminalFor(root)?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "term-ready":
				TerminalFor(root)?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "diff-resolved":
				string diffId = root.GetProperty("id").GetString() ?? string.Empty;
				bool kept = root.TryGetProperty("kept", out var keptEl) && keptEl.GetBoolean();
				string? finalContents = root.TryGetProperty("finalContents", out var fcEl) ? fcEl.GetString() : null;
				_session?.DiffPresenter.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				int revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				_session?.FileOpener.Open(revealPath, revealLine);
				break;
			case "list-dir":
				_session?.ListDirectory(root.TryGetProperty("path", out var dirEl) ? dirEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "active-editor-changed":
				_session?.UpdateActiveEditor(root);
				break;
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "save-buffer":
				SaveBuffer(
					root.GetProperty("path").GetString() ?? string.Empty,
					root.TryGetProperty("content", out var bufEl) ? bufEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "window-control":
				_shell?.HandleWindowControl(root);
				break;
			case "window-resize":
				_shell?.HandleWindowResize(root);
				break;
			case "menu-action":
				_shell?.HandleMenuAction(root);
				break;
			case "request-file-index":
				_shell?.PushFileIndex();
				break;
			case "latency-live":
			case "benchmark-result":
				// Per-tick perf telemetry (latency-live fires ~2x/sec) — noise unless we're profiling.
				if (_debugPerformance) {
					Console.WriteLine($"[weavie] {json}");
					Console.Out.Flush();
				}
				break;
			case "ready":
				// The page's bridge listener is live; push the persisted layout so it restores on launch, and
				// the initial window state so the title bar's maximize glyph + blur dim start correct.
				PushLayoutToWeb();
				PushWindowState();
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
			case "layout-changed":
				HandleLayoutChanged(root);
				break;
			default:
				// log — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
		}
	}

	/// <summary>Routes a terminal message to the controller for its <c>session</c> pane (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? pane = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return pane == "shell" ? _session?.Shell : _session?.Claude;
	}

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
	private void HandleLayoutChanged(JsonElement root) {
		if (!root.TryGetProperty("document", out var documentElement)) {
			return;
		}

		if (!LayoutSerialization.TryDeserialize(documentElement.GetRawText(), out var document, out string? error)
			|| document is null) {
			Console.WriteLine($"[weavie] layout-changed: bad document ({error})");
			return;
		}

		try {
			_layout.SetPanes(document.Root, document.Focused, LayoutSource.User);
		} catch (LayoutValidationException ex) {
			Console.WriteLine($"[weavie] layout-changed rejected: {ex.Message}");
		}
	}

	/// <summary>Pushes the persisted/reconciled layout document to the web app as a compact set-layout message.</summary>
	private void PushLayoutToWeb() {
		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_session is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_session.Changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>Pushes a targeted live-refresh of one edited file so its open editor model updates in place.</summary>
	private void PushRefreshToWeb(string path) {
		if (_session?.Changes.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.RefreshFile(change.Path, change.CurrentText));
		}
	}

	/// <summary>
	/// Autosave write: persists the editor buffer for <paramref name="path"/> to disk (constrained to the
	/// workspace) so the embedded claude sees the user's current state. A write that genuinely fails (or a
	/// path outside the workspace, which is a bug in the page) surfaces to the user as an error toast — an
	/// autosave must never silently lose the user's work.
	/// </summary>
	private void SaveBuffer(string path, string content) {
		// The session is created before the page can post any message — so a null here is a broken
		// invariant, not a runtime condition to absorb. Fail loud rather than silently drop the save.
		if (_session is null) {
			throw new InvalidOperationException("save-buffer arrived before the session was initialized.");
		}

		try {
			if (!BufferStore.Save(_session.FileSystem, _session.WorkspaceRoot, path, content)) {
				Notify("error", $"Couldn't save {Path.GetFileName(path)}: path is outside the workspace.");
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "notify", level, message }));

	private void PostDemoDiff() {
		const string original = "export function greet(name) {\n  return 'hi ' + name;\n}\n";
		const string proposed = "export function greet(name: string): string {\n  // weavie openDiff demo\n  return `hello, ${name}!`;\n}\n";
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream)) {
			writer.WriteStartObject();
			writer.WriteString("type", "show-diff");
			writer.WriteString("id", "demo");
			writer.WriteString("path", @"C:\src\weavie\demo\greet.ts");
			writer.WriteString("tabName", "✻ [Claude Code] greet.ts");
			writer.WriteString("original", original);
			writer.WriteString("proposed", proposed);
			writer.WriteEndObject();
		}

		_bridge.PostToWeb(Encoding.UTF8.GetString(stream.ToArray()));
	}

	private async Task CaptureSnapshotAsync() {
		string? dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		var core = _webView.CoreWebView2;
		if (core is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		string? name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		string path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

		try {
			await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
			await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fileStream);
			Console.WriteLine($"[weavie] snapshot saved: {path}");
			Console.Out.Flush();
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Console.Error.WriteLine($"[weavie] snapshot save failed: {ex.Message}");
		}
	}

	/// <summary>Fires <paramref name="action"/> once after <paramref name="seconds"/>, on the UI thread.</summary>
	private void ScheduleOnce(double seconds, Action action) {
		var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)(seconds * 1000)) };
		timer.Tick += (_, _) => {
			timer.Stop();
			timer.Dispose();
			action();
		};
		timer.Start();
	}

	/// <summary>
	/// Runs as the window closes — before the handle is destroyed and while the message pump is still
	/// alive. Persists geometry, then tears the WebView2 down deterministically. We can't leave this to
	/// the Form's automatic control disposal: that runs only after <see cref="Shutdown"/> (in
	/// FormClosed), as the message loop is already ending, which races WebView2's native teardown. When
	/// this instance owns its browser process and the page still has live web workers / sockets (Monaco's
	/// language workers, the LSP WebSocket), that race can leave a renderer thread alive — the window
	/// closes but weavie.exe never exits (you see teardown finish in the log, then the process just
	/// sits there). Disposing the control here closes the CoreWebView2 controller — terminating the
	/// renderer and releasing those threads — while the pump can still service the teardown, and before
	/// Shutdown kills the Vite dev server out from under the page.
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
			// Best-effort: a dispose mid-initialization (closed during the ~0.3s cold start) can fault,
			// and we're exiting regardless. Don't let it block the close.
			Console.Error.WriteLine($"[weavie] webview teardown: {ex.Message}");
		}
	}

	private void Shutdown() {
		_session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#if DEBUG
		_webDev?.Dispose();
#endif
	}
}
