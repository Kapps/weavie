using System.Text.Json;
using Weavie.Core.Shell;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Title-bar wire contract: host→web JSON and web→host menu parsing.</summary>
public sealed class ShellProtocolTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	private static JsonElement Config(string script) {
		const string prefix = "window.__WEAVIE_SHELL__ = ";
		Assert.StartsWith(prefix, script, StringComparison.Ordinal);
		Assert.EndsWith(";", script, StringComparison.Ordinal);
		return Parse(script[prefix.Length..^1]);
	}

	[Fact]
	public void BuildConfigScript_AssignsGlobalWithFields() {
		var config = Config(ShellProtocol.BuildConfigScript("win", WindowChrome.Frameless, "weavie", [@"C:\a", @"C:\b"], "0.1.247"));

		Assert.Equal("win", config.GetProperty("platform").GetString());
		Assert.Equal("weavie", config.GetProperty("workspaceLabel").GetString());
		Assert.Equal("0.1.247", config.GetProperty("buildNumber").GetString());
		Assert.False(config.TryGetProperty("titleBar", out _));
	}

	[Fact]
	public void BuildConfigScript_CarriesTheChromeInPageWireNames() {
		var chrome = Config(ShellProtocol.BuildConfigScript("win", WindowChrome.Frameless, "weavie", [], "0.1.247")).GetProperty("chrome");

		Assert.Equal(
			"""{"menus":true,"caption":"region","resizeHandles":true,"barHeight":null,"controls":{"start":[],"end":["minimize","maximize-toggle","close"]},"inset":{"start":0,"end":0}}""",
			chrome.GetRawText());
	}

	[Fact]
	public void BuildConfigScript_MeasuredChrome_CarriesHeightAndInset() {
		var measured = new WindowChrome(false, CaptionMode.Press, false, 52, WindowControls.None, new ChromeInset(78, 0));

		var chrome = Config(ShellProtocol.BuildConfigScript("mac", measured, "proj", [], "0.1.247")).GetProperty("chrome");

		Assert.Equal("press", chrome.GetProperty("caption").GetString());
		Assert.Equal(52, chrome.GetProperty("barHeight").GetInt32());
		Assert.Equal(78, chrome.GetProperty("inset").GetProperty("start").GetInt32());
	}

	[Fact]
	public void FixedWindow_OffersNoOperations() {
		IShellWindow window = new FixedShellWindow(WindowChrome.Browser);

		Assert.Same(WindowChrome.Browser, window.Chrome);
		Assert.Throws<NotSupportedException>(window.Minimize);
		Assert.Throws<NotSupportedException>(window.ToggleMaximize);
		Assert.Throws<NotSupportedException>(window.Close);
		Assert.Throws<NotSupportedException>(() => window.StartResize(ResizeEdge.Left));
		Assert.Throws<NotSupportedException>(() => window.PressCaption(new CaptionPress(CaptionPress.Primary, 1)));
	}

	[Fact]
	public void TryParseMenuAction_OpenRecent_CarriesPath() {
		var el = Parse("""{"action":"open-recent","path":"C:\\proj"}""");

		Assert.True(ShellProtocol.TryParseMenuAction(el, out var command, out string? path));
		Assert.Equal(MenuCommand.OpenRecent, command);
		Assert.Equal(@"C:\proj", path);
	}

	[Fact]
	public void TryParseMenuAction_UnknownAction_ReturnsFalse() =>
		Assert.False(ShellProtocol.TryParseMenuAction(Parse("""{"action":"nope"}"""), out _, out _));
}

/// <summary>Controller routing File-menu messages to the platform's menu actions.</summary>
public sealed class ShellMenuControllerTests {
	private sealed class FakeMenu : IShellMenuActions {
		public List<string> Calls { get; } = [];
		public string? OpenedPath { get; private set; }

		public void CloseWindow() => Calls.Add("close-window");
		public void Quit() => Calls.Add("quit");
		public void ShowOpenFolderPicker() => Calls.Add("open-folder");

		public void OpenWorkspace(string path) {
			Calls.Add("open-workspace");
			OpenedPath = path;
		}
	}

	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	private static ShellMenuController MakeMenu(FakeMenu menu) => new(menu);

	[Fact]
	public void HandleMenuAction_OpenFolder_ShowsPicker() {
		var menu = new FakeMenu();
		MakeMenu(menu).HandleMenuAction(Parse("""{"action":"open-folder"}"""));

		Assert.Equal(["open-folder"], menu.Calls);
	}

	[Fact]
	public void HandleMenuAction_OpenRecent_OpensThatWorkspace() {
		var menu = new FakeMenu();
		MakeMenu(menu).HandleMenuAction(Parse("""{"action":"open-recent","path":"C:\\proj"}"""));

		Assert.Equal("C:\\proj", menu.OpenedPath);
	}

	[Fact]
	public void HandleMenuAction_OpenRecent_EmptyPath_DoesNotOpen() {
		var menu = new FakeMenu();
		MakeMenu(menu).HandleMenuAction(Parse("""{"action":"open-recent","path":""}"""));

		Assert.Empty(menu.Calls);
		Assert.Null(menu.OpenedPath);
	}

	[Fact]
	public void HandleMenuAction_CloseWindow_UsesMenuClose() {
		var menu = new FakeMenu();
		MakeMenu(menu).HandleMenuAction(Parse("""{"action":"close-window"}"""));

		Assert.Equal(["close-window"], menu.Calls);
	}

	[Fact]
	public void HandleMenuAction_Exit_Quits() {
		var menu = new FakeMenu();
		MakeMenu(menu).HandleMenuAction(Parse("""{"action":"exit"}"""));

		Assert.Equal(["quit"], menu.Calls);
	}
}
