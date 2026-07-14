using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core;
using Weavie.Hosting.Web;
using Weavie.Win.Hosting;

namespace Weavie.Win;

/// <summary>
/// The app's empty state: an OS-chrome window rendering welcome.html in a WebView2, with no session until a
/// folder is opened. The shared <see cref="WelcomeController"/> injects the recents and routes the page's
/// <c>menu-action</c> messages; closing it with nothing else open quits the app (see <see cref="AppController"/>).
/// </summary>
internal sealed class WelcomeWindow : Form, IWebSurface {
	// Synthetic host for the virtual-host mapping; mirrors WorkspaceWindow / the macOS app:// scheme.
	private const string AppHost = "weavie.dev";

	// Maps the WKWebView script-message API the shared frontend speaks onto WebView2's postMessage.
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

	// Fraction of the screen's working area the window opens at — "about half the screen".
	private const double WidthFraction = 0.5;
	private const double HeightFraction = 0.62;

	// Painted on host surfaces before load so the WebView2 cold-start shows dark, matching welcome.html's splash.
	private static readonly Color StartupBackground = Color.FromArgb(0x00, 0x00, 0x00);

	private readonly AppController _app;
	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
	private WelcomeController? _controller;
	private bool _webViewTornDown;

	public WelcomeWindow(AppController app) {
		ArgumentNullException.ThrowIfNull(app);
		_app = app;

		Text = "weavie";
		Icon = AppIcon.Shared;
		BackColor = StartupBackground;
		MinimumSize = new Size(620, 460);
		StartPosition = FormStartPosition.Manual;

		_webView = new WebView2 {
			Dock = DockStyle.Fill,
			BackColor = StartupBackground,
			DefaultBackgroundColor = StartupBackground,
		};
		Controls.Add(_webView);

		Load += OnLoad;
		FormClosing += OnFormClosing;
	}

	/// <inheritdoc/>
	protected override void OnHandleCreated(EventArgs e) {
		base.OnHandleCreated(e);
		NativeChrome.UseDarkTitleBar(Handle);
	}

	/// <summary>
	/// Drops bare Alt/F10 menu-bar activation: this window has no menu bar, so menu mode would only freeze
	/// input and beep. Alt+Space still opens the system menu.
	/// </summary>
	protected override void WndProc(ref Message m) {
		if (CustomChrome.HandleSysKeyMenu(ref m)) {
			return;
		}

		base.WndProc(ref m);
	}

	private async void OnLoad(object? sender, EventArgs e) {
		SizeToScreen();
		try {
			await InitializeAsync();
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] welcome initialization failed: {ex}");
			MessageBox.Show(this, ex.ToString(), "weavie failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	/// <summary>Opens the window at a fraction of the primary screen's working area, centered on it.</summary>
	private void SizeToScreen() {
		var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
		int w = Math.Max((int)(area.Width * WidthFraction), MinimumSize.Width);
		int h = Math.Max((int)(area.Height * HeightFraction), MinimumSize.Height);
		Bounds = new Rectangle(
			area.X + ((area.Width - w) / 2),
			area.Y + ((area.Height - h) / 2),
			w,
			h);
	}

	private async Task InitializeAsync() {
		string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		// SetVirtualHostNameToFolderMapping throws if the folder is absent; ensure it exists so a build
		// without web assets still opens (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
#if DEBUG
		core.Settings.AreDevToolsEnabled = true; // local debugging, Debug builds only
#else
		core.Settings.AreDevToolsEnabled = false;
#endif
		core.Settings.IsStatusBarEnabled = false;

		_bridge.Attach(_webView);
		_controller = new WelcomeController(
			_bridge, this, $"https://{AppHost}/welcome.html", () => _app.Recents.Items,
			() => _app.OpenFolderInteractive(this), OpenRecent);

		// Always the bundled wwwroot over https://weavie.dev/; the empty state never probes for a Vite dev server.
		await _controller.ShowAsync();
	}

	// Open Recent from the welcome page: open the folder, else (OpenOrFocus prunes a folder that's gone) refresh the
	// list so the dead row disappears. A successful open closes this window (CloseWelcome).
	private void OpenRecent(string path) {
		// _controller is set in InitializeAsync before any message can route here, so it is non-null by now.
		if (_app.OpenOrFocus(path) is null) {
			_ = _controller!.RefreshAsync();
		}
	}

	// IWebSurface — the WebView2 ops the shared welcome flow drives; WebView2 is UI-thread-affine, so each marshals.
	void IWebSurface.Navigate(string url) => RunOnUi(() => _webView.CoreWebView2?.Navigate(url));

	void IWebSurface.RenderHtml(string html) => RunOnUi(() => _webView.CoreWebView2?.NavigateToString(html));

	Task IWebSurface.InjectStartupScriptAsync(string script) {
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		RunOnUi(async () => {
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

	private void RunOnUi(Action action) {
		if (InvokeRequired) {
			BeginInvoke(action);
		} else {
			action();
		}
	}

	/// <summary>Tears the WebView2 down deterministically before the handle is destroyed.</summary>
	private void OnFormClosing(object? sender, FormClosingEventArgs e) {
		if (_webViewTornDown) {
			return;
		}

		_webViewTornDown = true;
		_controller?.Detach();
		_bridge.Dispose();
		try {
			_webView.Dispose();
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] welcome webview teardown: {ex.Message}");
		}
	}
}
