using Microsoft.Web.WebView2.Core;
using Weavie.Core;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// The WebView2 bring-up: environment + virtual-host mapping, the bridge shim, the Debug Vite dev server (with
// reconnect recovery), building the shared core's backend, injecting the bootstrap, and navigation — plus the
// unattended screenshot. Split from WorkspaceWindow.cs so the chrome/lifecycle file stays focused.
internal sealed partial class WorkspaceWindow {
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
		// SetVirtualHostNameToFolderMapping throws if the folder is absent. Ensure it exists so a build without
		// web assets still opens the window (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		// WebView2 needs a writable user-data folder (the exe may live under Program Files); keep it under the
		// Weavie root so all Weavie data lives together (~/.weavie/internals/webview2).
		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		// Serve the built web app from wwwroot over https://weavie.app/ (no network, no localhost port), the
		// WebView2 counterpart of the macOS app:// scheme handler.
		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
		core.Settings.AreDevToolsEnabled = true;          // local debugging of the prototype
		core.Settings.IsStatusBarEnabled = false;
		// Let the web title bar declare its draggable caption via CSS `app-region: drag`; WebView2 then handles
		// window dragging, double-click-maximize, and the right-click system menu for the frameless window.
		core.Settings.IsNonClientRegionSupportEnabled = true;

		// Page origin: the shipped app loads the bundled web app over https://weavie.app/. In Debug the host
		// owns a Vite dev server (started here, torn down on exit) and points the WebView at it for hot-module
		// reload, falling back to the bundled wwwroot if the server can't start.
		string pageOrigin = $"https://{AppHost}";
#if DEBUG
		_webDev = new WebDevServer(line => {
			Console.WriteLine($"[vite] {line}");
			Console.Out.Flush();
		});
		string? devOrigin = await _webDev.StartAsync();
		if (devOrigin is not null) {
			pageOrigin = devOrigin;
			_devOrigin = devOrigin;
			// Recover a reload that fails because the dev server became unreachable (e.g. a reused server the
			// user's terminal owned was Ctrl+C'd, then Ctrl+F5). Bundle navigation (https) never hits this.
			core.NavigationCompleted += OnNavigationCompleted;
			Console.WriteLine($"[weavie] hot reload: serving web from {devOrigin} (Vite dev server)");
		} else {
			Console.WriteLine("[weavie] dev server unavailable; falling back to bundled wwwroot");
		}
#endif

		_bridge.Attach(_webView);

		// Build the live backend (sessions / IDE-MCP / LSP) and wire the bridge. Awaited without ConfigureAwait
		// so the continuation resumes on the UI thread for the WebView2 calls below (StartAsync has no UI affinity).
		await _core.StartAsync(pageOrigin);

		// Inject the bootstrap globals (fonts / editor / theme / lsp / commands / keybindings / shell) before
		// navigation so both surfaces mount at the user's settings with no flash.
		await core.AddScriptToExecuteOnDocumentCreatedAsync(_core.BuildBootstrap());

		// Off-by-default diagnostic (a real setting, not a buried env var): tell the web app to log its own
		// startup phases via ?startuptiming.
		string qs = _app.Settings.GetBool("diagnostics.startupTiming", false) ? "?startuptiming=1" : string.Empty;
		core.Navigate($"{pageOrigin}/index.html{qs}");

		// Unattended screenshot for the deliverable; gated on WEAVIE_SHOT_DIR so the shipped app never writes one.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
			ScheduleOnce(delay, () => _ = CaptureSnapshotAsync());
		}
	}

#if DEBUG
	// Recover when a navigation to the Vite dev origin fails because the server is unreachable — the case behind
	// "localhost could not be reached" on a hard reload (Ctrl+F5/Ctrl+R) after a reused dev server died. Revive
	// the dev server (StartAsync reuses a live one, else respawns) and reload; if it can't come back, fall back
	// to the always-mapped bundle and log loudly. Only wired in Debug, so the shipped app can never reach it.
	private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
		if (e.IsSuccess) {
			_devRecoveryAttempts = 0; // a good load ends the burst; the next failure starts a fresh count
			return;
		}

		// Act only on connection-class failures (the server is gone) — not user cancels or HTTP errors, which
		// navigate fine against a live server and would otherwise trigger a needless bundle fallback.
		if (_devOrigin is null || _recoveringDevServer || _webDev is null
			|| e.WebErrorStatus is not (
				CoreWebView2WebErrorStatus.CannotConnect
				or CoreWebView2WebErrorStatus.ServerUnreachable
				or CoreWebView2WebErrorStatus.HostNameNotResolved
				or CoreWebView2WebErrorStatus.ConnectionAborted
				or CoreWebView2WebErrorStatus.ConnectionReset
				or CoreWebView2WebErrorStatus.Disconnected
				or CoreWebView2WebErrorStatus.Timeout)) {
			return;
		}

		var core = _webView.CoreWebView2;
		if (core is null) {
			return;
		}

		_recoveringDevServer = true;
		try {
			Console.WriteLine($"[weavie] dev server at {_devOrigin} unreachable ({e.WebErrorStatus}); reviving for reload");
			// Cap revival attempts so a server that comes up then immediately fails again can't spin forever.
			string? revived = _devRecoveryAttempts < 3 ? await _webDev.StartAsync() : null;
			if (revived is not null) {
				_devRecoveryAttempts++;
				_devOrigin = revived;
				core.Navigate($"{revived}/index.html");
			} else {
				// Dev server is gone for good: stop chasing it and load the bundle that's always mapped.
				_devOrigin = null;
				core.NavigationCompleted -= OnNavigationCompleted;
				Console.WriteLine($"[weavie] dev server could not be revived; loading bundled wwwroot at https://{AppHost}");
				core.Navigate($"https://{AppHost}/index.html");
			}
		} finally {
			_recoveringDevServer = false;
		}
	}
#endif

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
}
