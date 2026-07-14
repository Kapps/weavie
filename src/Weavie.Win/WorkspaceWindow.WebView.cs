using Microsoft.Web.WebView2.Core;
using Weavie.Core;
using Weavie.Hosting;
using Weavie.Hosting.Web;

namespace Weavie.Win;

// The WebView2 bring-up: environment + virtual-host mapping, bridge shim + attach, then the shared web launcher
// (Weavie.Hosting.Web) that owns the dev-server / bootstrap / navigation flow. This host supplies only the native
// WebView2 ops via IWebSurface, the Debug dev-loss recovery, and the unattended screenshot.
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
		// SetVirtualHostNameToFolderMapping throws if the folder is absent; ensure it exists so a build without web
		// assets still opens (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		// WebView2 needs a writable user-data folder (the exe may live under Program Files), kept under the Weavie
		// root (~/.weavie/internals/webview2).
		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		// Start the WebView2 runtime (its browser process spins up out-of-process) and overlap the backend
		// bring-up (sessions + git) with it rather than waiting — the two largest serial startup costs are
		// independent. StartAsync is idempotent, so the web launcher below joins this same run.
		var environmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		_ = _core.StartAsync();
		var environment = await environmentTask;
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
#if DEBUG
		core.Settings.AreDevToolsEnabled = true; // local debugging, Debug builds only
#else
		core.Settings.AreDevToolsEnabled = false;
#endif
		core.Settings.IsStatusBarEnabled = false;
		// Let the web title bar declare its draggable caption via CSS `app-region: drag`; WebView2 then handles
		// dragging, double-click-maximize, and the right-click system menu for the frameless window.
		core.Settings.IsNonClientRegionSupportEnabled = true;

		// A window.open / target=_blank goes to the OS browser, never a second in-app WebView with bridge access.
		core.NewWindowRequested += OnNewWindowRequested;
#if !DEBUG
		// First-party app: in Release, a top-level navigation to a foreign web origin (an injected link/redirect)
		// is cancelled and handed to the OS browser, so it can never keep bridge access. (Debug has its own
		// dev-server NavigationStarting handler + dev origin, so the gate is Release-only.)
		core.NavigationStarting += OnNavigationStarting;
#endif

		// Wire the web↔host bridge before bring-up; the shared launcher then starts the backend, injects the
		// bootstrap, and navigates (Weavie.Hosting.Web.WebAppLauncher).
		_bridge.Attach(_webView);

		string indexQuery = _app.Settings.GetBool("diagnostics.startupTiming", false) ? "?startuptiming=1" : string.Empty;
		var launcher = new WebAppLauncher(this, _core, indexQuery);

#if DEBUG
		// In Debug the host owns a Vite dev server for hot-module reload. If it can't come up, DevWebBringUp renders
		// a loud error page rather than silently serving the (possibly stale) bundle; its Retry / Load-stale-bundle
		// links (weavie-dev://) route back here.
		_devBringUp = new DevWebBringUp(
			launcher, this,
			DevWebRoot.Resolve(System.Reflection.Assembly.GetExecutingAssembly()),
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
		await launcher.LaunchBundleAsync();
#endif

		ScheduleSnapshotIfRequested();
	}

	// IWebSurface — the native WebView2 ops the shared bring-up drives. Each marshals onto the UI thread (WebView2
	// calls are UI-thread-affine), so the shared flow in Weavie.Hosting.Web stays thread-agnostic.
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

	// External URLs (window.open / target=_blank) open in the OS browser, http(s) only; an in-app new window
	// (which would share the bridge) is never created.
	private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) {
		e.Handled = true;
		OpenExternalIfWeb(e.Uri);
	}

#if !DEBUG
	private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
		// Allow the app host and non-web schemes (about:/data: for NavigateToString error pages); cancel a
		// top-level navigation to any other web origin and route it to the OS browser instead.
		if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			|| (Uri.TryCreate(_core.WorkspaceOrigin, UriKind.Absolute, out var workspace)
				&& uri.Scheme == workspace.Scheme && uri.Host == workspace.Host && uri.Port == workspace.Port)) {
			return;
		}

		e.Cancel = true;
		OpenExternalIfWeb(e.Uri);
	}
#endif

	private void OpenExternalIfWeb(string? uri) {
		if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
			&& (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)) {
			((IHostPlatform)this).OpenExternalUrl(parsed.AbsoluteUri);
		}
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
		Console.WriteLine("[weavie] loading the STALE bundled workspace app (explicit developer choice)");
		await _devBringUp.LoadBundleAsync();
	}

	// Recover when navigation to the Vite dev origin fails because the server is unreachable (e.g. a hard reload
	// after it died): revive it and reload, else fall back to the always-mapped bundle. Only wired in Debug.
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
				// Dev server is gone for good: stop chasing it and load the shared server's bundle.
				_devOrigin = null;
				core.NavigationCompleted -= OnNavigationCompleted;
				Console.WriteLine("[weavie] dev server could not be revived; loading the bundled workspace app");
				await _devBringUp.LoadBundleAsync();
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
