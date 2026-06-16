using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Win.Hosting;

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

	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
	private TerminalController? _terminal;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private IdeIntegration? _ide;

	public MainForm() {
		Text = "weavie";
		ClientSize = new Size(1280, 840);
		StartPosition = FormStartPosition.CenterScreen;

		_webView = new WebView2 { Dock = DockStyle.Fill };
		Controls.Add(_webView);

		Load += OnLoad;
		FormClosed += (_, _) => Shutdown();
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
		var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
		// SetVirtualHostNameToFolderMapping throws if the folder is absent. Ensure it exists so a
		// build without web assets still opens the window (navigation 404s) instead of crashing.
		Directory.CreateDirectory(wwwroot);

		// WebView2 needs a writable user-data folder; the exe may live under Program Files.
		var userDataFolder = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "weavie", "WebView2");
		Directory.CreateDirectory(userDataFolder);

		var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
		await _webView.EnsureCoreWebView2Async(environment);
		var core = _webView.CoreWebView2;

		// Serve the built web app from wwwroot over https://weavie.app/ (no network, no localhost
		// port), the WebView2 counterpart of the macOS app:// scheme handler.
		core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);
		await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeShim);
		core.Settings.AreDevToolsEnabled = true;          // local debugging of the prototype
		core.Settings.IsStatusBarEnabled = false;

		_bridge.Attach(_webView);
		_terminal = new TerminalController(_bridge);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the
		// discovery env so the spawned claude connects to us (the SOLE edit feed).
		var fileSystem = new LocalFileSystem();
		var workspace = TerminalController.ResolveWorkspace();
		_terminal.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		_ide = new IdeIntegration(_diffPresenter, fileSystem, [workspace], "weavie");
		_ide.Server.Log += line => {
			Console.WriteLine($"[mcp] {line}");
			Console.Out.Flush();
		};
		_terminal.ExtraEnvironment = _ide.EnvironmentVariables;
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; workspace {workspace}; lock {_ide.LockFilePath}");

		var query = new List<string>();
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_AUTOBENCH"))) {
			query.Add("autobench=1");
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_FPSPROBE"))) {
			query.Add("fpsprobe=1");
		}
		var qs = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
		core.Navigate($"https://{AppHost}/index.html{qs}");

		// Unattended screenshot for the deliverable; gated on WEAVIE_SHOT_DIR so the shipped app
		// never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			var delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out var d) ? d : 4.0;
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
				var input = Convert.FromBase64String(root.GetProperty("dataB64").GetString() ?? string.Empty);
				if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_INPUT"))) {
					var printable = string.Concat(input.Select(b => b is >= 0x20 and < 0x7f ? ((char)b).ToString() : $"\\x{b:x2}"));
					Console.WriteLine($"[weavie] term-input <- xterm ({input.Length}B): {printable}");
					Console.Out.Flush();
				}
				_terminal?.Write(input);
				break;
			case "term-resize":
				_terminal?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "term-ready":
				_terminal?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "diff-resolved":
				var diffId = root.GetProperty("id").GetString() ?? string.Empty;
				var kept = root.TryGetProperty("kept", out var keptEl) && keptEl.GetBoolean();
				var finalContents = root.TryGetProperty("finalContents", out var fcEl) ? fcEl.GetString() : null;
				_diffPresenter?.Resolve(diffId, kept, finalContents);
				break;
			case "reveal-file":
				var revealPath = root.GetProperty("path").GetString() ?? string.Empty;
				var revealLine = root.TryGetProperty("line", out var lnEl) ? lnEl.GetInt32() : 1;
				_fileOpener?.Open(revealPath, revealLine);
				break;
			default:
				// benchmark-result / latency-live / log / ready — surface for unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
		}
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
		var dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		var core = _webView.CoreWebView2;
		if (core is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		var name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		var path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

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

	private void Shutdown() {
		_terminal?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
	}
}
