using System.Text.Json;
using CoreGraphics;
using Foundation;
using Weavie.Core.Changes;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Weavie.Core.Mcp;
using Weavie.Mac.Hosting;
using WebKit;
using LayoutGeometry = Weavie.Core.Layout.WindowState;

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
	private SettingsStore? _settings;
	private TerminalController? _claude;
	private TerminalController? _shell;
	private McpDiffPresenter? _diffPresenter;
	private FileOpener? _fileOpener;
	private IdeIntegration? _ide;
	private LayoutStore? _layout;
	private EditorStore? _editor;
	private SessionChangeTracker? _changes;
	private LocalFileSystem? _fileSystem;
	private string? _workspace;
	private NSWindow? _window;
	private WKWebView? _webView;

	/// <summary>
	/// Creates the host window and WKWebView, registers the <c>app://</c> scheme handler and
	/// <c>weavie</c> script-message bridge, starts the terminal and IDE-MCP server (injecting its
	/// discovery env into the spawned claude), and loads the web app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		string resourcePath = NSBundle.MainBundle.ResourcePath
			?? throw new InvalidOperationException("No bundle resource path.");
		string wwwroot = Path.Combine(resourcePath, "wwwroot");

		var config = new WKWebViewConfiguration();
		config.SetUrlSchemeHandler(new AppSchemeHandler(wwwroot), "app");
		config.UserContentController.AddScriptMessageHandler(_bridge, "weavie");
		// Allow the Web Inspector for local debugging of the prototype.
		config.Preferences.SetValueForKey(NSNumber.FromBoolean(true), new NSString("developerExtrasEnabled"));

		// Restore the saved window geometry (size / position / zoomed) when present and on-screen, else
		// fall back to a centered 1280x840 default.
		_layout = LayoutPanes.CreateStore();
		var savedWindow = _layout.Current.Window;
		bool usedSaved = savedWindow is not null
			&& IsOnScreen(new CGRect(savedWindow.X, savedWindow.Y, savedWindow.Width, savedWindow.Height));
		var frame = usedSaved
			? new CGRect(savedWindow!.X, savedWindow.Y, savedWindow.Width, savedWindow.Height)
			: new CGRect(0, 0, 1280, 840);
		_webView = new WKWebView(frame, config);
		_bridge.Attach(_webView);

		// User settings (shell / workspace / claude path) resolved from ~/.weavie/settings.toml; the
		// store is the change hub the host reacts to (e.g. a shell change reopens the shell pane).
		_settings = CoreSettings.CreateStore();
		_settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Typography: inject the resolved editor + terminal fonts at document start so both surfaces
		// mount at the user's font with no default-font flash; live changes are pushed below.
		config.UserContentController.AddUserScript(new WKUserScript(
			new NSString($"window.__WEAVIE_FONTS__ = {FontSettings.BuildJson(_settings)};"),
			WKUserScriptInjectionTime.AtDocumentStart,
			isForMainFrameOnly: true));

		_claude = new TerminalController(_bridge, "claude", _settings);
		_shell = new TerminalController(_bridge, "shell", _settings);
		_bridge.MessageReceived += OnWebMessage;

		// IDE-MCP: start the loopback server + lock file, render openDiff to Monaco, and inject
		// the discovery env so the spawned claude connects to us (the SOLE edit feed). The same store
		// backs the settings MCP tools, so the user can change settings by talking to claude.
		var fileSystem = new LocalFileSystem();
		_fileSystem = fileSystem;
		string workspace = _settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_workspace = workspace;
		_claude.Workspace = workspace;
		_shell.Workspace = workspace;
		_fileOpener = new FileOpener(_bridge, fileSystem, workspace);
		_diffPresenter = new McpDiffPresenter(_bridge, fileSystem, _fileOpener);
		// Tracks the editor's active file + selection (fed by the page) so the IDE-MCP server can tell
		// the spawned claude what the user is looking at.
		_editor = new EditorStore();
		_ide = new IdeIntegration(new PermissionModeDiffPresenter(_diffPresenter, _settings), [workspace], "weavie", _settings, _layout, _editor);
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
		// Hook bridge: a --settings file whose hooks route claude's tool calls to our relay; the observed
		// stream is logged here (the session change view will consume the same feed).
		_claude.SettingsFilePath = _ide.WriteSettingsFile();
		_ide.HookBridge.Observed += request => {
			Console.WriteLine($"[hook] {request.Event} {request.ToolName}");
			Console.Out.Flush();
		};
		_ide.HookBridge.Log += line => {
			Console.WriteLine($"[hook] {line}");
			Console.Out.Flush();
		};

		// Session change tracking: the same hook stream feeds the tracker (baseline at PreToolUse, new
		// content at PostToolUse), independent of openDiff and permission mode. Changed pushes the change
		// list; FileChanged pushes a targeted live-refresh of the one edited file. Events arrive off the
		// main thread; marshal before touching the web.
		_changes = new SessionChangeTracker(fileSystem);
		_ide.HookBridge.Observed += _changes.Observe;
		_changes.Changed += () => InvokeOnMainThread(PushChangesToWeb);
		_changes.FileChanged += path => InvokeOnMainThread(() => PushRefreshToWeb(path));
		// Inline diff: per-turn diff per edited file + clear-all on a turn boundary (implicit accept).
		_changes.FileChanged += path => InvokeOnMainThread(() => PushTurnDiffToWeb(path));
		_changes.TurnBegan += () => InvokeOnMainThread(PushTurnReset);
		Console.WriteLine($"[weavie] IDE-MCP on 127.0.0.1:{_ide.Port}; registry on 127.0.0.1:{_ide.RegistryPort}; workspace {workspace}; lock {_ide.LockFilePath}");

		// Reaction wiring: a changed shell (ApplyMode.ReopensTerminal) reopens the shell pane live.
		// Settings events arrive off the main thread, so marshal onto it before touching the controller.
		_settings.Subscribe("terminal.shell", _ => InvokeOnMainThread(() => _shell?.Restart()));

		// Fonts (ApplyMode.Live): any global or per-surface font change re-pushes the resolved editor +
		// terminal fonts to the web app, which applies them in place. PostToWeb marshals to the main
		// thread itself and the store is thread-safe, so the off-thread change event can call it directly.
		_settings.SettingChanged += change => {
			if (FontSettings.Keys.Contains(change.Key)) {
				_bridge.PostToWeb(FontSettings.BuildJson(_settings, "fonts"));
			}
		};

		// Layout: push the canonical document to the web when the store changes (a reconciled web edit or
		// a future MCP setLayout). Change events arrive off the main thread.
		_layout.Changed += _ => InvokeOnMainThread(PushLayoutToWeb);

		_window = new NSWindow(
			frame,
			NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
			NSBackingStore.Buffered,
			false) {
			Title = "weavie",
			ContentView = _webView,
		};
		if (!usedSaved) {
			_window.Center();
		} else if (savedWindow is { Maximized: true }) {
			_window.Zoom(null);
		}

		// Persist geometry on resize-end and on close; SetWindow no-ops when nothing actually changed.
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.DidEndLiveResizeNotification, _ => SaveWindowState(), _window);
		NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification, _ => SaveWindowState(), _window);

		_window.MakeKeyAndOrderFront(null);

		nint screenHz = (_window.Screen ?? NSScreen.MainScreen)?.MaximumFramesPerSecond ?? 0;
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
		string qs = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
		_webView.LoadRequest(new NSUrlRequest(new NSUrl($"app://app/index.html{qs}")));

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot for the deliverable: fire from the native run loop (not a JS
		// timer, which throttles when the window is occluded). Gated on WEAVIE_SHOT_DIR so the
		// shipped app never writes screenshots.
		if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR"))) {
			double delay = double.TryParse(Environment.GetEnvironmentVariable("WEAVIE_SHOT_DELAY"), out double d) ? d : 4.0;
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
		SaveWindowState();
		_claude?.Dispose();
		_shell?.Dispose();
		_ide?.DisposeAsync().AsTask().GetAwaiter().GetResult();
		_settings?.Dispose();
	}

	private void SaveWindowState() {
		if (_window is null || _layout is null || _window.IsMiniaturized) {
			return;
		}

		_layout.SetWindow(CaptureWindowState());
	}

	/// <summary>Snapshots the current geometry, keeping the prior un-zoomed restore bounds while zoomed.</summary>
	private LayoutGeometry CaptureWindowState() {
		if (_window!.IsZoomed && _layout!.Current.Window is { } prior) {
			return prior with { Maximized = true };
		}

		var frame = _window.Frame;
		return new LayoutGeometry {
			X = (int)frame.X,
			Y = (int)frame.Y,
			Width = (int)frame.Width,
			Height = (int)frame.Height,
			Maximized = _window.IsZoomed,
		};
	}

	private static bool IsOnScreen(CGRect frame) =>
		frame.Width > 0 && frame.Height > 0
		&& NSScreen.Screens.Any(screen => screen.VisibleFrame.IntersectsWith(frame));

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
			case "active-editor-changed":
				if (_editor is not null && ActiveEditor.TryParse(root, out var activeEditor) && activeEditor is not null) {
					_editor.SetActive(activeEditor);
				}

				break;
			case "get-change-diff":
				PushChangeDiffToWeb(root.GetProperty("path").GetString() ?? string.Empty);
				break;
			case "save-buffer":
				SaveBuffer(
					root.GetProperty("path").GetString() ?? string.Empty,
					root.TryGetProperty("content", out var bufEl) ? bufEl.GetString() ?? string.Empty : string.Empty);
				break;
			case "accept-turn":
				AcceptTurn();
				break;
			case "undo-turn":
				UndoTurn();
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

	/// <summary>Applies a layout the web sent (split/focus change) through the store, which validates + persists it.</summary>
	private void HandleLayoutChanged(JsonElement root) {
		if (_layout is null || !root.TryGetProperty("document", out var documentElement)) {
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
		if (_layout is null) {
			return;
		}

		string documentJson = LayoutSerialization.SerializeCompact(_layout.Current);
		_bridge.PostToWeb($"{{\"type\":\"set-layout\",\"document\":{documentJson}}}");
	}

	/// <summary>Pushes the session change list (each file's path + added/removed line counts) to the page.</summary>
	private void PushChangesToWeb() {
		if (_changes is not null) {
			_bridge.PostToWeb(ChangeMessages.SessionChanges(_changes));
		}
	}

	/// <summary>Pushes one file's session diff (baseline vs. current text) to the page for the changes view.</summary>
	private void PushChangeDiffToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.ChangeDiff(change));
		}
	}

	/// <summary>Pushes a targeted live-refresh of one edited file so its open editor model updates in place.</summary>
	private void PushRefreshToWeb(string path) {
		if (_changes?.Get(path) is { } change) {
			_bridge.PostToWeb(ChangeMessages.RefreshFile(change.Path, change.CurrentText));
		}
	}

	/// <summary>Pushes one file's per-turn diff so the page renders it inline in the live editor.</summary>
	private void PushTurnDiffToWeb(string path) {
		if (_changes?.GetTurn(path) is { } turn) {
			_bridge.PostToWeb(ChangeMessages.TurnDiff(turn));
		}
	}

	/// <summary>Clears all inline turn markers on a turn boundary (the prior turn is implicitly accepted).</summary>
	private void PushTurnReset() => _bridge.PostToWeb(ChangeMessages.TurnReset());

	/// <summary>Accepts the whole turn's changes: resets the per-turn baseline and clears the page's inline markers.</summary>
	private void AcceptTurn() {
		if (_changes is null) {
			return;
		}

		_changes.AcceptTurn();
		_bridge.PostToWeb(ChangeMessages.TurnReset());
	}

	/// <summary>
	/// Undoes the whole turn's changes: reverts every file touched this turn to its turn baseline on disk and
	/// live-refreshes the editor. Files created this turn truncate to empty (not deleted) — surfaced via a toast.
	/// </summary>
	private void UndoTurn() {
		if (_changes is null || _fileSystem is null || _workspace is null) {
			return;
		}

		var truncated = new List<string>();
		foreach (var change in _changes.TurnChanges()) {
			try {
				if (!BufferStore.Save(_fileSystem, _workspace, change.Path, change.BaselineText)) {
					Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: path is outside the workspace.");
					continue;
				}

				if (change.BaselineText.Length == 0) {
					truncated.Add(Path.GetFileName(change.Path));
				}

				_changes.RecordChange(change.Path);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				Notify("error", $"Couldn't undo {Path.GetFileName(change.Path)}: {ex.Message}");
			}
		}

		if (truncated.Count > 0) {
			Notify("warn", $"Undo emptied {truncated.Count} file(s) created this turn (delete manually): {string.Join(", ", truncated)}");
		}
	}

	/// <summary>
	/// Autosave write: persists the editor buffer for <paramref name="path"/> to disk (constrained to the
	/// workspace) so the embedded claude sees the user's current state. A write that genuinely fails (or a
	/// path outside the workspace, which is a bug in the page) surfaces to the user as an error toast — an
	/// autosave must never silently lose the user's work.
	/// </summary>
	private void SaveBuffer(string path, string content) {
		// The session is wired in DidFinishLaunching, before the page can post any message — so a null here
		// is a broken invariant, not a runtime condition to absorb. Fail loud rather than drop the save.
		if (_fileSystem is null || _workspace is null) {
			throw new InvalidOperationException("save-buffer arrived before the session was initialized.");
		}

		try {
			if (!BufferStore.Save(_fileSystem, _workspace, path, content)) {
				Notify("error", $"Couldn't save {Path.GetFileName(path)}: path is outside the workspace.");
			}
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Notify("error", $"Couldn't save {Path.GetFileName(path)}: {ex.Message}");
		}
	}

	/// <summary>Pushes a user-facing notification (rendered as a toast in the page).</summary>
	private void Notify(string level, string message) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "notify", level, message }));

	/// <summary>Routes a terminal message to the controller for its <c>session</c> (default: claude).</summary>
	private TerminalController? TerminalFor(JsonElement root) {
		string? session = root.TryGetProperty("session", out var s) ? s.GetString() : null;
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
		string? dir = Environment.GetEnvironmentVariable("WEAVIE_SHOT_DIR");
		if (_webView is null || string.IsNullOrEmpty(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		string? name = Environment.GetEnvironmentVariable("WEAVIE_SHOT_NAME");
		string path = Path.Combine(dir, string.IsNullOrEmpty(name) ? "step1-latency.png" : name);

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
