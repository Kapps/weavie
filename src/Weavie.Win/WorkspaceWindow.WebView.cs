using Microsoft.Web.WebView2.Core;
using Weavie.Core;
using Weavie.Hosting;
using Weavie.Hosting.Web;

namespace Weavie.Win;

// The WebView2 bring-up: environment + virtual-host mapping, the bridge shim + attach, then the shared web
// launcher (Weavie.Hosting.Web), which owns the dev-server / bootstrap / navigation flow. This host supplies
// only the native WebView2 ops via IWebSurface, the Debug dev-loss reconnect recovery, and the unattended screenshot.
internal sealed partial class WorkspaceWindow : IWebSurface {
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
		// web assets still opens (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		// WebView2 needs a writable user-data folder (the exe may live under Program Files); keep it under the
		// Weavie root (~/.weavie/internals/webview2).
		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		// Serve the built web app from wwwroot over https://weavie.app/ (no network, no localhost port), the
		// WebView2 counterpart of the macOS app:// scheme handler.
		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
		core.Settings.AreDevToolsEnabled = true;          // local debugging
		core.Settings.IsStatusBarEnabled = false;
		// Let the web title bar declare its draggable caption via CSS `app-region: drag`; WebView2 then handles
		// window dragging, double-click-maximize, and the right-click system menu for the frameless window.
		core.Settings.IsNonClientRegionSupportEnabled = true;

		// Wire the web↔host message bridge before bring-up; the shared launcher then starts the backend,
		// injects the bootstrap, and navigates — see Weavie.Hosting.Web.WebAppLauncher.
		_bridge.Attach(_webView);

		string indexQuery = _app.Settings.GetBool("diagnostics.startupTiming", false) ? "?startuptiming=1" : string.Empty;
		var launcher = new WebAppLauncher(this, _core, indexQuery);

#if DEBUG
		// In Debug the host owns a Vite dev server for hot-module reload. If it can't come up we do NOT silently
		// serve the (possibly stale) bundled wwwroot — DevWebBringUp renders a loud error page instead, and the
		// host wires its Retry / Load-stale-bundle links (weavie-dev://) back to it.
		_devBringUp = new DevWebBringUp(
			launcher, this,
			DevWebRoot.Resolve(System.Reflection.Assembly.GetExecutingAssembly()),
			$"https://{AppHost}",
			line => {
				Console.WriteLine($"[vite] {line}");
				Console.Out.Flush();
			});
		core.NavigationStarting += OnDevRecoveryNavigationStarting;
		string? devOrigin = await _devBringUp.RunAsync();
		if (devOrigin is not null) {
			core.NavigationStarting -= OnDevRecoveryNavigationStarting;
			_devOrigin = devOrigin;
			// Recover a reload that fails because the dev server became unreachable (e.g. Ctrl+C'd, then Ctrl+F5).
			core.NavigationCompleted += OnNavigationCompleted;
			Console.WriteLine($"[weavie] hot reload: serving web from {devOrigin} (Vite dev server)");
		}
#else
		await launcher.LaunchAsync($"https://{AppHost}");
#endif

		ScheduleSnapshotIfRequested();
	}

	// IWebSurface — the native WebView2 operations the shared web bring-up drives. Each marshals onto the UI
	// thread (WebView2 calls are UI-thread-affine), so the shared flow in Weavie.Hosting.Web stays thread-agnostic.
	void IWebSurface.Navigate(string url) =>
		_dispatcher.Post(() => _webView.CoreWebView2?.Navigate(url));

	void IWebSurface.RenderHtml(string html) =>
		_dispatcher.Post(() => _webView.CoreWebView2?.NavigateToString(html));

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_dispatcher.Post(async () => {
			try {
				if (_webView.CoreWebView2 is { } core) {
					await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
				}

				tcs.SetResult();
			} catch (Exception ex) {
				tcs.SetException(ex);
			}
		});
		return tcs.Task;
	}

	/// <summary>Schedules the unattended deliverable screenshot when WEAVIE_SHOT_DIR is set (never in the shipped app).</summary>
	private void ScheduleSnapshotIfRequested() {
		if (ScreenshotRequest.FromEnvironment() is { } shot) {
			ScheduleOnce(shot.DelaySeconds, () => _ = CaptureSnapshotAsync(shot));
		}
	}

#if DEBUG
	/// <summary>Intercepts the error page's <c>weavie-dev://</c> action links (Retry / Load stale bundle); every
	/// other navigation passes through untouched.</summary>
	private async void OnDevRecoveryNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
		string uri = e.Uri ?? string.Empty;
		if (uri.StartsWith(DevWebBringUp.RetryUrl, StringComparison.OrdinalIgnoreCase)) {
			e.Cancel = true;
			await RetryDevServerAsync();
		} else if (uri.StartsWith(DevWebBringUp.BundleUrl, StringComparison.OrdinalIgnoreCase)) {
			e.Cancel = true;
			await LoadStaleBundleAsync();
		}
	}

	/// <summary>Retry button: ask the shared bring-up to try the dev server again. On success, stop intercepting
	/// and arm the dev-loss recovery; on failure it re-renders the error page itself.</summary>
	private async Task RetryDevServerAsync() {
		var core = _webView.CoreWebView2;
		if (core is null || _devBringUp is null) {
			return;
		}

		Console.WriteLine("[weavie] retrying Vite dev server…");
		string? devOrigin = await _devBringUp.RunAsync();
		if (devOrigin is not null) {
			core.NavigationStarting -= OnDevRecoveryNavigationStarting;
			_devOrigin = devOrigin;
			core.NavigationCompleted += OnNavigationCompleted;
			Console.WriteLine($"[weavie] hot reload: serving web from {devOrigin} (Vite dev server)");
		}
	}

	/// <summary>"Load stale bundle anyway" button: the developer explicitly accepts the possibly-stale bundle.</summary>
	private async Task LoadStaleBundleAsync() {
		var core = _webView.CoreWebView2;
		if (core is null || _devBringUp is null) {
			return;
		}

		core.NavigationStarting -= OnDevRecoveryNavigationStarting;
		Console.WriteLine($"[weavie] loading STALE bundled wwwroot at https://{AppHost} (explicit developer choice)");
		await _devBringUp.LoadBundleAsync();
	}

	// Recover when a navigation to the Vite dev origin fails because the server is unreachable (e.g. a hard
	// reload after the dev server died). Revive it (same origin, backend still valid) and reload; if it can't
	// come back, load the always-mapped bundle and log loudly. Only wired in Debug.
	private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
		if (e.IsSuccess) {
			_devRecoveryAttempts = 0; // a good load ends the burst; the next failure starts fresh
			return;
		}

		// Act only on connection-class failures (the server is gone) — not user cancels or HTTP errors, which
		// navigate fine against a live server and would otherwise trigger a needless bundle fallback.
		if (_devOrigin is null || _recoveringDevServer || _devBringUp is null
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
			string? revived = _devRecoveryAttempts < 3 ? await _devBringUp.ReviveAsync() : null;
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

	private async Task CaptureSnapshotAsync(ScreenshotRequest shot) {
		var core = _webView.CoreWebView2;
		if (core is null) {
			return;
		}

		Directory.CreateDirectory(shot.DirectoryPath);
		try {
			await using var fileStream = new FileStream(shot.TargetPath, FileMode.Create, FileAccess.Write);
			await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fileStream);
			Console.WriteLine($"[weavie] snapshot saved: {shot.TargetPath}");
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
