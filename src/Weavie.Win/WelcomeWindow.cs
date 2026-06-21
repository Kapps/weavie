using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core;
using Weavie.Win.Hosting;

namespace Weavie.Win;

/// <summary>
/// The app's empty state: a standalone, ordinary OS-chrome window (not the frameless workspace chrome)
/// whose body is a WebView2 rendering the shared web app's welcome screen (welcome.html). Deliberately has
/// <em>no</em> session — no terminals, Claude, MCP, or LSP load until a folder is opened. The web view
/// drives the app back through the same <c>menu-action</c> bridge messages the title-bar File menu uses
/// (open-folder / open-recent), so the welcome UI is written once and shared with macOS. Closing it with
/// nothing else open quits the app (see <see cref="AppController"/>).
/// </summary>
internal sealed class WelcomeWindow : Form {
	// Synthetic host for the virtual-host mapping; mirrors WorkspaceWindow / the macOS app:// scheme.
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

	// Fraction of the screen's working area the window opens at — "about half the screen".
	private const double WidthFraction = 0.5;
	private const double HeightFraction = 0.62;

	// The welcome page's dark background, painted on the host surfaces before the page loads so the
	// WebView2 cold-start shows dark instead of white. Matches welcome.html's pre-JS splash.
	private static readonly Color StartupBackground = Color.FromArgb(0x00, 0x00, 0x00);

	private readonly AppController _app;
	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
	private bool _webViewTornDown;
	private string? _recentsScriptId;

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
	/// Drops bare Alt/F10 menu-bar activation, as the workspace window does: this window has no menu bar
	/// (just the web welcome screen), so entering menu mode would only freeze input and beep. Alt+Space still
	/// opens the system menu.
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
		// without web assets still opens the window (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		string userDataFolder = WeaviePaths.Internal("webview2");
		Directory.CreateDirectory(userDataFolder);

		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
		core.Settings.AreDevToolsEnabled = true;
		core.Settings.IsStatusBarEnabled = false;

		_bridge.Attach(_webView);
		_bridge.MessageReceived += OnWebMessage;

		// Recents reach the page as window.__WEAVIE_WELCOME__, injected before navigation (no flash, no
		// round-trip). Re-injected + reloaded if a recent is later pruned (RefreshRecentsAsync).
		await InjectRecentsAsync();

		// The welcome screen is the empty state — no session, and no dev server of its own — so it always serves
		// the bundled wwwroot over https://weavie.app/. Each workspace window owns its own per-instance Vite dev
		// server in Debug; the welcome deliberately doesn't probe for or reuse one (a cross-process reuse is
		// exactly the worktree cross-talk we removed).
		core.Navigate($"https://{AppHost}/welcome.html");
	}

	/// <summary>(Re)injects the current recents as <c>window.__WEAVIE_WELCOME__</c> for the next document load.</summary>
	private async Task InjectRecentsAsync() {
		var core = _webView.CoreWebView2;
		if (core is null) {
			return;
		}

		if (_recentsScriptId is not null) {
			core.RemoveScriptToExecuteOnDocumentCreated(_recentsScriptId);
		}

		string json = JsonSerializer.Serialize(new { recents = _app.Recents.Items });
		_recentsScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_WELCOME__ = {json};");
	}

	private void OnWebMessage(string json) {
		JsonElement root;
		string type;
		try {
			using var doc = JsonDocument.Parse(json);
			root = doc.RootElement.Clone();
			type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
		} catch (JsonException) {
			Console.WriteLine($"[weavie] (welcome, unparsed) {json}");
			return;
		}

		switch (type) {
			case "menu-action":
				HandleMenuAction(root);
				break;
			default:
				// ready / log / anything else — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] (welcome) {json}");
				Console.Out.Flush();
				break;
		}
	}

	/// <summary>Routes the web's <c>menu-action</c> (Open Folder / Open Recent) to the app's open logic.</summary>
	private void HandleMenuAction(JsonElement root) {
		string action = root.TryGetProperty("action", out var a) ? a.GetString() ?? string.Empty : string.Empty;
		switch (action) {
			case "open-folder":
				_app.OpenFolderInteractive(this);
				break;
			case "open-recent":
				string? path = root.TryGetProperty("path", out var p) ? p.GetString() : null;
				// OpenOrFocus returns null when the folder is gone (and prunes its recents entry); refresh
				// the list so the dead row disappears. A successful open closes this window (CloseWelcome).
				if (!string.IsNullOrEmpty(path) && _app.OpenOrFocus(path) is null) {
					_ = RefreshRecentsAsync();
				}

				break;
		}
	}

	/// <summary>Re-injects the current recents and reloads the page so a pruned entry drops out of the list.</summary>
	private async Task RefreshRecentsAsync() {
		await InjectRecentsAsync();
		_webView.CoreWebView2?.Reload();
	}

	/// <summary>Tears the WebView2 down deterministically before the handle is destroyed (mirrors WorkspaceWindow).</summary>
	private void OnFormClosing(object? sender, FormClosingEventArgs e) {
		if (_webViewTornDown) {
			return;
		}

		_webViewTornDown = true;
		try {
			_webView.Dispose();
		} catch (Exception ex) {
			Console.Error.WriteLine($"[weavie] welcome webview teardown: {ex.Message}");
		}
	}
}
