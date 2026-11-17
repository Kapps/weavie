using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for native host actions, driving the same web messages the page sends and asserting the
/// host routes them to the platform with UI affinity.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreTerminalActionsTests {
	[Fact]
	public async Task ClipboardWrite_WritesTheTextToThePlatform() {
		await using var host = await TestHost.StartAsync();

		host.HostEvent("clipboard", "write", new { text = "copied from the terminal" });

		Assert.Equal("copied from the terminal", host.Platform.LastWrittenClipboard);
	}

	[Fact]
	public async Task ClipboardRead_RepliesWithTheClipboardContentTaggedById() {
		await using var host = await TestHost.StartAsync();
		host.Platform.ClipboardValue = "paste me";

		var reply = await host.HostRequestAsync<System.Text.Json.JsonElement>(
			"clipboard",
			"read",
			new { });

		Assert.Equal("paste me", reply.GetProperty("text").GetString());
	}

	[Theory]
	[InlineData("https://example.com/auth?code=abc")]
	[InlineData("http://localhost:8080/callback")]
	public async Task OpenUrl_OpensHttpUrlsViaThePlatform(string url) {
		await using var host = await TestHost.StartAsync();

		host.HostEvent("platform", "openUrl", new { url });

		Assert.Equal(url, host.Platform.LastOpenedUrl);
	}

	[Theory]
	[InlineData("file:///C:/Windows/System32/calc.exe")]
	[InlineData("file://attacker/share/evil.exe")]
	[InlineData("ms-msdt:/id PCWDiagnostic")]
	[InlineData("javascript:alert(1)")]
	[InlineData("C:\\Windows\\System32\\calc.exe")]
	[InlineData("not a url")]
	public async Task OpenUrl_RefusesNonHttpSchemes(string url) {
		await using var host = await TestHost.StartAsync();

		host.HostEvent("platform", "openUrl", new { url });

		Assert.Null(host.Platform.LastOpenedUrl); // the OS opener was never reached
	}

	[Fact]
	public async Task MalformedMessage_IsContainedAndTheHostKeepsWorking() {
		await using var host = await TestHost.StartAsync();

		// Bad base64 in term-input throws inside the dispatch; the backstop must contain it (it would otherwise
		// crash the network-exposed worker), and the host keeps handling subsequent messages.
		host.SessionEvent(
			host.WorkspaceSession,
			ShellTerminalSet.FeatureName(host.SelectedSession.Shells.Primary!.Id),
			"input",
			new { dataB64 = "!!! not base64 !!!" });
		host.HostEvent("clipboard", "write", new { text = "still working" });

		Assert.Equal("still working", host.Platform.LastWrittenClipboard);
	}

	[Fact]
	public async Task NativeHostActionsEnterTheUiDispatcherAfterMessageBusAdmission() {
		var errors = new ConcurrentQueue<Exception>();
		var dispatcher = new SerialUiDispatcher(errors.Enqueue);
		var uiThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		dispatcher.Post(() => uiThread.SetResult(Environment.CurrentManagedThreadId));
		int expectedThread = await uiThread.Task;
		var window = new ThreadRecordingWindow();
		await using var host = TestHost.CreateUnstarted(dispatcher, window);
		host.Platform.MenuActions = window;
		int? readyThread = null;
		host.Core.Ready += () => readyThread = Environment.CurrentManagedThreadId;
		await host.Core.StartAsync();
		await host.ConnectAsync();
		host.Platform.ClipboardValue = "paste me";
		host.Platform.ClipboardImageValue = new ClipboardImage("image/png", [1, 2, 3]);

		host.HostEvent("clipboard", "write", new { text = "copied" });
		await host.HostRequestAsync<System.Text.Json.JsonElement>("clipboard", "read", new { });
		await host.HostRequestAsync<System.Text.Json.JsonElement>("clipboard", "readImage", new { });
		host.HostEvent("platform", "openUrl", new { url = "https://example.com" });
		host.HostEvent("window", "control", new { action = "minimize" });
		host.HostEvent("window", "resize", new { edge = "bottom-right" });
		host.HostEvent("window", "menu", new { action = "open-folder" });
		await Wait.UntilAsync(() =>
			host.Platform.LastOpenedUrl is not null
			&& window.Threads.Count == 3);

		Assert.Equal(expectedThread, readyThread);
		Assert.Equal(expectedThread, host.Platform.ClipboardWriteThread);
		Assert.Equal(expectedThread, host.Platform.ClipboardReadThread);
		Assert.Equal(expectedThread, host.Platform.ClipboardImageReadThread);
		Assert.Equal(expectedThread, host.Platform.OpenUrlThread);
		Assert.All(window.Threads, thread => Assert.Equal(expectedThread, thread));
		Assert.Empty(host.UiDispatchFailures);
		Assert.Empty(errors);
	}

	[Fact]
	public async Task FailedHostActionShowsActionableError() {
		await using var host = await TestHost.StartAsync();
		host.Platform.ToggleWindowThrows = true;
		await host.HostRequestAsync<JsonElement>("commands", "invoke",
			new { id = CoreCommands.ToggleWindow, args = (object?)null });
		var toast = await Wait.ForAsync<JsonElement>(() => {
			var found = host.UiDispatchFailures.FirstOrDefault();
			return found.ValueKind == JsonValueKind.Object ? found : null;
		});
		Assert.Contains("the window is gone", toast.GetProperty("message").GetString(), StringComparison.Ordinal);
		Assert.Equal("View Logs", toast.GetProperty("action").GetProperty("label").GetString());
	}

	[Fact]
	public async Task MenuActionsDoNotRequireCustomWindowControls() {
		await using var host = TestHost.CreateUnstarted();
		var menu = new ThreadRecordingWindow();
		host.Platform.MenuActions = menu;
		await host.Core.StartAsync();
		await host.ConnectAsync();

		host.HostEvent("window", "menu", new { action = "open-folder" });
		await Wait.UntilAsync(() => menu.Threads.Count == 1);

		Assert.IsType<FixedShellWindow>(host.Platform.Window);
	}

	[Theory]
	[InlineData("left", ResizeEdge.Left)]
	[InlineData("right", ResizeEdge.Right)]
	[InlineData("top", ResizeEdge.Top)]
	[InlineData("bottom", ResizeEdge.Bottom)]
	[InlineData("top-left", ResizeEdge.TopLeft)]
	[InlineData("top-right", ResizeEdge.TopRight)]
	[InlineData("bottom-left", ResizeEdge.BottomLeft)]
	[InlineData("bottom-right", ResizeEdge.BottomRight)]
	public async Task ResizeEventNamesTheEdge(string edge, ResizeEdge expected) {
		var window = new ThreadRecordingWindow();
		await using var host = TestHost.CreateUnstarted(window);
		await host.Core.StartAsync();
		await host.ConnectAsync();

		host.HostEvent("window", "resize", new { edge });

		await Wait.UntilAsync(() => window.Calls.Count == 1);
		Assert.Equal([$"resize:{expected}"], window.Calls);
	}

	[Fact]
	public async Task WindowEventsReachTheWindowAndUnknownActionsAreRejected() {
		var window = new ThreadRecordingWindow();
		await using var host = TestHost.CreateUnstarted(window);
		await host.Core.StartAsync();
		await host.ConnectAsync();

		host.HostEvent("window", "control", new { action = "spin" });
		host.HostEvent("window", "control", new { action = "maximize-toggle" });
		host.HostEvent("window", "control", new { action = "close" });
		host.HostEvent("window", "captionPress", new { button = 2, count = 1 });

		await Wait.UntilAsync(() => window.Calls.Count == 3);
		Assert.Equal(["maximize", "close", "press:2:1"], window.Calls);
	}

	[Fact]
	public async Task ChromeIsPushedAtHelloAndOnEveryChange() {
		var window = new ThreadRecordingWindow();
		await using var host = TestHost.CreateUnstarted(window);
		await host.Core.StartAsync();
		await host.ConnectAsync();
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("window", "chrome").Any(chrome =>
			chrome.GetProperty("caption").GetString() == "press"
			&& chrome.GetProperty("inset").GetProperty("start").GetInt32() == 78));

		window.Change(window.Chrome with { Inset = new ChromeInset(0, 0), BarHeight = 40 });

		await Wait.UntilAsync(() => host.Bridge.PostedEvents("window", "chrome").Any(chrome =>
			chrome.GetProperty("barHeight").ValueKind == JsonValueKind.Number
			&& chrome.GetProperty("barHeight").GetInt32() == 40));
	}

	private sealed class ThreadRecordingWindow : IShellWindow, IShellMenuActions {
		public ConcurrentQueue<int> Threads { get; } = [];
		public ConcurrentQueue<string> Calls { get; } = [];
		public WindowChrome Chrome { get; private set; } = new(
			false, CaptionMode.Press, false, null, WindowControls.None, new ChromeInset(78, 0));
		public event Action? ChromeChanged;

		public void Change(WindowChrome chrome) {
			Chrome = chrome;
			ChromeChanged?.Invoke();
		}

		public void Minimize() => Record("minimize");
		public void ToggleMaximize() => Record("maximize");
		public void StartResize(ResizeEdge edge) => Record($"resize:{edge}");
		public void Close() => Record("close");
		public void PressCaption(CaptionPress press) => Record($"press:{press.Button}:{press.Count}");
		public void CloseWindow() => Record("close-window");
		public void Quit() => Record("quit");
		public void ShowOpenFolderPicker() => Record("open-folder");
		public void OpenWorkspace(string path) => Record("open-workspace");

		private void Record(string call) {
			Threads.Enqueue(Environment.CurrentManagedThreadId);
			Calls.Enqueue(call);
		}
	}
}
