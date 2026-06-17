using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Lsp;
using Weavie.Core.Mcp;
using Weavie.Win.Hosting;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

namespace Weavie.Win;

/// <summary>
/// The Windows host window: a single full-bleed WebView2 rendering the shared Vite web app, wired
/// to the same Core seams as the macOS app — a real ConPTY terminal running <c>claude</c>, the
/// loopback IDE-MCP server + lock file, and Monaco diff/file presentation. This is the Windows
/// analogue of the macOS <c>AppDelegate</c>.
/// </summary>
internal sealed class MainForm : Form {
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

	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
	private readonly LayoutStore _layout;
	private bool _lastMaximized;
	private bool _webViewTornDown;
	private SettingsStore? _settings;
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private IdeIntegration? _ide;
	private LspBridgeServer? _lsp;
#if DEBUG
	private WebDevServer? _webDev;
#endif

	// The app's dark background, painted on every host surface before the page loads so the ~0.25s of
	// WebView2 cold-start shows dark instead of the default white. Matches the web splash + styles --bg;
	// when theme persistence lands the host can swap in the user's resolved theme background here.
	private static readonly Color StartupBackground = Color.FromArgb(0x1e, 0x1e, 0x1e);

	public MainForm() {
		Text = "weavie";
		BackColor = StartupBackground;

		// Restore the saved window geometry (size / position / maximized) before the handle is created;
		// a missing or off-screen saved state falls back to a centered 1280x840 default.
		_layout = LayoutPanes.CreateStore();
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

		Load += OnLoad;
		ResizeEnd += (_, _) => SaveWindowState();
		SizeChanged += OnWindowSizeChanged;
		FormClosing += OnFormClosing;
		FormClosed += (_, _) => Shutdown();
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
		}
	}

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

		// User settings (shell / workspace / claude path) resolved from ~/.weavie/settings.toml; the
		// store is the change hub the host reacts to (e.g. a shell change reopens the shell pane).
		_settings = CoreSettings.CreateStore();
		_settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		// Off-by-default diagnostic (a real setting, not a buried env var): when on, the host logs its
		// launch phases below and tells the web app to log its own via ?startuptiming.
		bool startupTiming = _settings.GetBool("diagnostics.startupTiming");
		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the
		// discovery env so the spawned claude connects to us (the SOLE edit feed). The same store backs
		// the settings MCP tools, so the user can change settings by talking to claude.
		var fileSystem = new LocalFileSystem();
		string workspace = _settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		_ide = new IdeIntegration(new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout);
		_ide.Server.Log += line => {
			Console.WriteLine($"[mcp] {line}");
			Console.Out.Flush();
		};
		if (_ide.RegistryServer is not null) {
			_ide.RegistryServer.Log += line => {
				Console.WriteLine($"[registry] {line}");
				Console.Out.Flush();
			};
		}

		_claude.ExtraEnvironment = _ide.EnvironmentVariables;
		// Capability registry: hand the spawned claude an --mcp-config pointing at the registry server
		// so the settings tools reach the model as mcp__weavie__* (the IDE server's tools are filtered).
		_claude.McpConfigPath = _ide.WriteMcpConfigFile();
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// LSP bridge: a loopback WS↔stdio proxy that spawns language servers (bring-your-own, resolved
		// on PATH) and pipes them to monaco-languageclient in the page. Inject discovery — the port, a
		// per-session token, and the workspace root — before navigation; mirrors the IDE-MCP loopback +
		// token posture (bind 127.0.0.1, require the token on the WS upgrade; origin pinned to the app).
		string lspToken = IdeLockFile.NewAuthToken();
		_lsp = new LspBridgeServer(lspToken, workspace, allowedOrigin: pageOrigin);
		_lsp.Log += line => {
			Console.WriteLine($"[lsp] {line}");
			Console.Out.Flush();
		};
		int lspPort = _lsp.Start();
		// Advertise the catalog so the page can lazily start a client per language (on first matching
		// document) and feed each server its default settings as initializationOptions + the answer to
		// workspace/configuration (e.g. gopls needs {"semanticTokens":true}; spec §15).
		var servers = LanguageServerCatalog.All.Select(d => new {
			id = d.Id,
			languageIds = d.LanguageIds,
			settings = string.IsNullOrEmpty(d.DefaultSettingsJson) ? null : JsonNode.Parse(d.DefaultSettingsJson),
		});
		string lspConfig = JsonSerializer.Serialize(new { url = $"ws://127.0.0.1:{lspPort}", token = lspToken, workspace, servers });
		await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_LSP__ = {lspConfig};");
		Console.WriteLine($"[weavie] LSP bridge on 127.0.0.1:{lspPort}; workspace {workspace}");

		// Typography: inject the resolved editor + terminal fonts before navigation so both surfaces
		// mount at the user's font with no default-font flash; live changes are pushed below.
		await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings)};");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the UI thread, so marshal onto it before touching the controller.
		_settings.Subscribe("terminal.shell", _ => {
			if (InvokeRequired) {
				BeginInvoke(() => _shell?.Restart());
			} else {
				_shell?.Restart();
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
				_diffPresenter?.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				string revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				int revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				_fileOpener?.Open(revealPath, revealLine);
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
				// The page's bridge listener is live; push the persisted layout so it restores on launch.
				PushLayoutToWeb();
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

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
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
		_claude?.Dispose();
		_shell?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_lsp?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#if DEBUG
		_webDev?.Dispose();
#endif
		_settings?.Dispose();
	}
}
