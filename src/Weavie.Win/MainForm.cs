using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Weavie.Core;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Lsp;
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

	// Perf instrumentation (the live latency HUD and its per-tick log spam) is opt-in via
	// WEAVIE_DEBUG_PERFORMANCE: surfaced to the web app as ?debugperf, and used here to gate the
	// latency-live/benchmark-result console logging. Off by default so normal runs stay quiet.
	private readonly bool _debugPerformance =
		!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_PERFORMANCE"));

	private readonly HostBridge _bridge = new();
	private readonly WebView2 _webView;
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

		// WebView2 needs a writable user-data folder (the exe may live under Program Files); keep it
		// under the Weavie root so all Weavie data lives together (~/.weavie/internals/webview2).
		var userDataFolder = WeaviePaths.Internal("webview2");
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

		// Page origin: the shipped app loads the bundled web app over https://weavie.app/. In Debug the
		// host owns a Vite dev server — started here, torn down on exit, so there's no second terminal —
		// and points the WebView at it for hot-module reload, falling back to the bundled wwwroot if the
		// server can't start. Release loads the bundle (the block below is compiled out). The chosen
		// origin flows into navigation and the LSP bridge's allowed origin.
		var pageOrigin = $"https://{AppHost}";
#if DEBUG
		_webDev = new WebDevServer(line => {
			Console.WriteLine($"[vite] {line}");
			Console.Out.Flush();
		});
		var devOrigin = await _webDev.StartAsync();
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
		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject the
		// discovery env so the spawned claude connects to us (the SOLE edit feed). The same store backs
		// the settings MCP tools, so the user can change settings by talking to claude.
		var fileSystem = new LocalFileSystem();
		var workspace = _settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		_ide = new IdeIntegration(_diffPresenter, fileSystem, [workspace], "weavie", _settings);
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
		var lspToken = IdeLockFile.NewAuthToken();
		_lsp = new LspBridgeServer(lspToken, workspace, allowedOrigin: pageOrigin);
		_lsp.Log += line => {
			Console.WriteLine($"[lsp] {line}");
			Console.Out.Flush();
		};
		var lspPort = _lsp.Start();
		// Advertise the catalog so the page can lazily start a client per language (on first matching
		// document) and feed each server its default settings as initializationOptions + the answer to
		// workspace/configuration (e.g. gopls needs {"semanticTokens":true}; spec §15).
		var servers = LanguageServerCatalog.All.Select(d => new {
			id = d.Id,
			languageIds = d.LanguageIds,
			settings = string.IsNullOrEmpty(d.DefaultSettingsJson) ? null : JsonNode.Parse(d.DefaultSettingsJson),
		});
		var lspConfig = JsonSerializer.Serialize(new { url = $"ws://127.0.0.1:{lspPort}", token = lspToken, workspace, servers });
		await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__WEAVIE_LSP__ = {lspConfig};");
		Console.WriteLine($"[weavie] LSP bridge on 127.0.0.1:{lspPort}; workspace {workspace}");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the UI thread, so marshal onto it before touching the controller.
		_settings.Subscribe("terminal.shell", _ => {
			if (InvokeRequired) {
				BeginInvoke(() => _shell?.Restart());
			} else {
				_shell?.Restart();
			}
		});

		var query = new List<string>();
		if (_debugPerformance) {
			query.Add("debugperf=1");
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_AUTOBENCH"))) {
			query.Add("autobench=1");
		}
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_FPSPROBE"))) {
			query.Add("fpsprobe=1");
		}
		var qs = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
		core.Navigate($"{pageOrigin}/index.html{qs}");

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
				TerminalFor(root)?.Write(input);
				break;
			case "term-resize":
				TerminalFor(root)?.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
				break;
			case "term-ready":
				TerminalFor(root)?.Start(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32());
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
			case "latency-live":
			case "benchmark-result":
				// Per-tick perf telemetry (latency-live fires ~2x/sec) — noise unless we're profiling.
				if (_debugPerformance) {
					Console.WriteLine($"[weavie] {json}");
					Console.Out.Flush();
				}
				break;
			default:
				// log / ready — surface for diagnostics and unattended capture.
				Console.WriteLine($"[weavie] {json}");
				Console.Out.Flush();
				break;
		}
	}

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		var session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
		return session == "shell" ? _shell : _claude;
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
