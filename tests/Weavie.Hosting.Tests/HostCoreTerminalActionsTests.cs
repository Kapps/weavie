using System.Collections.Concurrent;
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
			host.PrimarySession,
			"terminal.shell",
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
		await using var host = TestHost.CreateUnstarted(dispatcher);
		var window = new ThreadRecordingWindow();
		host.Platform.Window = window;
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
		Assert.Empty(errors);
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

		Assert.Null(host.Platform.Window);
	}

	private sealed class ThreadRecordingWindow : IShellWindow, IShellMenuActions {
		public ConcurrentQueue<int> Threads { get; } = [];

		public void Minimize() => Record();
		public void ToggleMaximize() => Record();
		public void StartResize(ResizeEdge edge) => Record();
		public void Close() => Record();
		public void CloseWindow() => Record();
		public void Quit() => Record();
		public void ShowOpenFolderPicker() => Record();
		public void OpenWorkspace(string path) => Record();

		private void Record() => Threads.Enqueue(Environment.CurrentManagedThreadId);
	}
}
