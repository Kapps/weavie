using System.Text.Json;
using CoreGraphics;
using Foundation;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Mac.Hosting;
using WebKit;

namespace Weavie.Mac;

/// <summary>
/// The macOS application delegate: builds the WKWebView host window, wires the JS bridge,
/// terminal, file opener, and MCP diff presenter, and starts the IDE-MCP server so the spawned
/// claude connects back as the sole edit feed.
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : NSApplicationDelegate {
	// Perf instrumentation (the live latency HUD and its per-tick log spam) is opt-in via
	// WEAVIE_DEBUG_PERFORMANCE: surfaced to the web app as ?debugperf, and used here to gate the
	// latency-live/benchmark-result console logging. Off by default so normal runs stay quiet.
	private readonly bool _debugPerformance =
		!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEBUG_PERFORMANCE"));

	private readonly HostBridge _bridge = new();
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private IdeIntegration? _ide;
	private NSWindow? _window;
	private WKWebView? _webView;

	/// <summary>
	/// Creates the host window and WKWebView, registers the <c>app://</c> scheme handler and
	/// <c>weavie</c> script-message bridge, starts the terminal and IDE-MCP server (injecting its
	/// discovery env into the spawned claude), and loads the web app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		var resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		var wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
		// Allow the Web Inspector for local debugging of the prototype.
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));

		var frame = new CGRect(0, 0, 1280, 840);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);
		_claude = new TerminalController(_bridge, "claude");
		_shell = new TerminalController(_bridge, "shell");
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject
		// the discovery env so the spawned claude connects to us (the SOLE edit feed).
		var fileSystem = new LocalFileSystem();
		var workspace = TerminalController.ResolveWorkspace();
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		_ide = new IdeIntegration(_diffPresenter, fileSystem, [workspace], "weavie");
		_ide.Server.Log += line => {
			Console.WriteLine($"[mcp] {line}");
			Console.Out.Flush();
		};
		_claude.ExtraEnvironment = _ide.EnvironmentVariables;
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; workspace {workspace}; lock {_ide.LockFilePath}");

		_window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = "weavie",
			ContentView = _webView,
		};
		_window.Center();
		_window.MakeKeyAndOrderFront(null);

		var screenHz = (_window.Screen ?? NSScreen.MainScreen)?.MaximumFramesPerSecond ?? 0;
		Console.WriteLine($"[weavie] NSScreen.maximumFramesPerSecond = {screenHz}");

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
		_webView.LoadRequest(new NSUrlRequest(new NSUrl($"app://app/index.html{qs}")));

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot for the deliverable: fire from the native run loop (not a JS
		// timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
		// shipped app never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			var delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out var d) ? d : 4.0;
			NSTimer.CreateScheduledTimer(delay, repeats: false, _ => CaptureSnapshot());
		}

		// Dev aid: render a sample openDiff so the Monaco diff UI can be screenshotted without
		// driving claude. Gated on WEAVIE_DEMO_DIFF; never fires in normal use.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_DEMO_DIFF"))) {
			NSTimer.CreateScheduledTimer(2.5, repeats: false, _ => PostDemoDiff());
		}
	}

	/// <summary>Quits the app when its last (only) window is closed.</summary>
	public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

	/// <summary>Disposes both terminals and shuts down the IDE-MCP server on app exit.</summary>
	public override void WillTerminate(NSNotification notification) {
		_claude?.Dispose();
		_shell?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
			writer.WriteString("path", "/Users/kapps/src/weavie/demo/greet.ts");
			writer.WriteString("tabName", "✻ [Claude Code] greet.ts");
			writer.WriteString("original", original);
			writer.WriteString("proposed", proposed);
			writer.WriteEndObject();
		}

		_bridge.PostToWeb(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
	}

	private void CaptureSnapshot() {
		var dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		if (_webView is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		var name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		var path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

		_webView.TakeSnapshot(new WKSnapshotConfiguration(), (image, error) => {
			if (image is null) {
				Console.Error.WriteLine($"[weavie] snapshot failed: {error?.LocalizedDescription}");
				return;
			}

			var tiff = image.AsTiff();
			if (tiff is null) {
				return;
			}

			using var rep = new NSBitmapImageRep(tiff);
			var png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
			if (png is null) {
				Console.Error.WriteLine("[weavie] snapshot: PNG encoding failed");
				return;
			}

			if (png.Save(path, true, out var saveError)) {
				Console.WriteLine($"[weavie] snapshot saved: {path}");
			} else {
				Console.Error.WriteLine($"[weavie] snapshot save failed: {saveError?.LocalizedDescription}");
			}

			Console.Out.Flush();
		});
	}
}
