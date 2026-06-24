using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Title-bar wire contract: host→web JSON and web→host action parsing.</summary>
public sealed class ShellProtocolTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void BuildConfigScript_AssignsGlobalWithFields() {
		string script = ShellProtocol.BuildConfigScript("win", "custom", "weavie", [@"C:\a", @"C:\b"]);

		Assert.StartsWith("window.__WEAVIE_SHELL__ = ", script);
		Assert.EndsWith(";", script);
		Assert.Contains("\"platform\":\"win\"", script);
		Assert.Contains("\"titleBar\":\"custom\"", script);
		Assert.Contains("\"workspaceLabel\":\"weavie\"", script);
	}

	[Fact]
	public void BuildConfigScript_NullTitleBar_SerializesNull() {
		string script = ShellProtocol.BuildConfigScript("mac", null, "proj", []);

		Assert.Contains("\"titleBar\":null", script);
	}

	[Fact]
	public void BuildWindowState_CarriesTypeAndFlags() {
		var el = Parse(ShellProtocol.BuildWindowState(maximized: true, focused: false));

		Assert.Equal("window-state", el.GetProperty("type").GetString());
		Assert.True(el.GetProperty("maximized").GetBoolean());
		Assert.False(el.GetProperty("focused").GetBoolean());
	}

	[Fact]
	public void BuildFileIndex_CarriesRootAndFiles() {
		var el = Parse(ShellProtocol.BuildFileIndex(@"C:\w", [@"C:\w\a.txt", @"C:\w\b.txt"]));

		Assert.Equal("file-index", el.GetProperty("type").GetString());
		Assert.Equal(@"C:\w", el.GetProperty("root").GetString());
		Assert.Equal(2, el.GetProperty("files").GetArrayLength());
	}

	[Theory]
	[InlineData("minimize", WindowControl.Minimize)]
	[InlineData("maximize-toggle", WindowControl.MaximizeToggle)]
	[InlineData("close", WindowControl.Close)]
	public void TryParseWindowControl_KnownActions(string action, WindowControl expected) {
		var el = Parse($$"""{"type":"window-control","action":"{{action}}"}""");

		Assert.True(ShellProtocol.TryParseWindowControl(el, out var control));
		Assert.Equal(expected, control);
	}

	[Fact]
	public void TryParseWindowControl_UnknownAction_ReturnsFalse() =>
		Assert.False(ShellProtocol.TryParseWindowControl(Parse("""{"action":"spin"}"""), out _));

	[Theory]
	[InlineData("left", ResizeEdge.Left)]
	[InlineData("right", ResizeEdge.Right)]
	[InlineData("top", ResizeEdge.Top)]
	[InlineData("bottom", ResizeEdge.Bottom)]
	[InlineData("top-left", ResizeEdge.TopLeft)]
	[InlineData("top-right", ResizeEdge.TopRight)]
	[InlineData("bottom-left", ResizeEdge.BottomLeft)]
	[InlineData("bottom-right", ResizeEdge.BottomRight)]
	public void TryParseWindowResize_KnownEdges(string edge, ResizeEdge expected) {
		var el = Parse($$"""{"type":"window-resize","edge":"{{edge}}"}""");

		Assert.True(ShellProtocol.TryParseWindowResize(el, out var parsed));
		Assert.Equal(expected, parsed);
	}

	[Fact]
	public void TryParseWindowResize_UnknownEdge_ReturnsFalse() =>
		Assert.False(ShellProtocol.TryParseWindowResize(Parse("""{"edge":"sideways"}"""), out _));

	[Fact]
	public void TryParseMenuAction_OpenRecent_CarriesPath() {
		var el = Parse("""{"type":"menu-action","action":"open-recent","path":"C:\\proj"}""");

		Assert.True(ShellProtocol.TryParseMenuAction(el, out var command, out string? path));
		Assert.Equal(MenuCommand.OpenRecent, command);
		Assert.Equal(@"C:\proj", path);
	}

	[Fact]
	public void TryParseMenuAction_UnknownAction_ReturnsFalse() =>
		Assert.False(ShellProtocol.TryParseMenuAction(Parse("""{"action":"nope"}"""), out _, out _));
}

/// <summary>Controller routing title-bar messages to the platform window + file index.</summary>
public sealed class ShellControllerTests {
	private sealed class FakeWindow : IShellWindow {
		public List<string> Calls { get; } = [];
		public string? OpenedPath { get; private set; }

		public void Minimize() => Calls.Add("minimize");
		public void ToggleMaximize() => Calls.Add("toggle-maximize");
		public void StartResize(ResizeEdge edge) => Calls.Add($"start-resize:{edge}");
		public void Close() => Calls.Add("close");
		public void CloseWindow() => Calls.Add("close-window");
		public void Quit() => Calls.Add("quit");
		public void ShowOpenFolderPicker() => Calls.Add("open-folder");

		public void OpenWorkspace(string path) {
			Calls.Add("open-workspace");
			OpenedPath = path;
		}
	}

	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	private static ShellController Make(FakeWindow window, out List<string> posted, IFileSystem? fileSystem = null) {
		var posts = new List<string>();
		posted = posts;
		var index = new WorkspaceFileIndex(fileSystem ?? new InMemoryFileSystem(), "/w");
		return new ShellController(window, index, posts.Add);
	}

	[Fact]
	public void HandleWindowControl_Minimize_CallsWindow() {
		var window = new FakeWindow();
		Make(window, out _).HandleWindowControl(Parse("""{"action":"minimize"}"""));

		Assert.Equal(["minimize"], window.Calls);
	}

	[Fact]
	public void HandleWindowControl_MaximizeToggle_TogglesWindow() {
		var window = new FakeWindow();
		Make(window, out _).HandleWindowControl(Parse("""{"action":"maximize-toggle"}"""));

		Assert.Equal(["toggle-maximize"], window.Calls);
	}

	[Fact]
	public void HandleMenuAction_OpenFolder_ShowsPicker() {
		var window = new FakeWindow();
		Make(window, out _).HandleMenuAction(Parse("""{"action":"open-folder"}"""));

		Assert.Equal(["open-folder"], window.Calls);
	}

	[Fact]
	public void HandleWindowResize_KnownEdge_StartsResize() {
		var window = new FakeWindow();
		Make(window, out _).HandleWindowResize(Parse("""{"type":"window-resize","edge":"bottom-right"}"""));

		Assert.Equal(["start-resize:BottomRight"], window.Calls);
	}

	[Fact]
	public void HandleWindowResize_UnknownEdge_DoesNothing() {
		var window = new FakeWindow();
		Make(window, out _).HandleWindowResize(Parse("""{"type":"window-resize","edge":"nope"}"""));

		Assert.Empty(window.Calls);
	}

	[Fact]
	public void HandleMenuAction_OpenRecent_OpensThatWorkspace() {
		var window = new FakeWindow();
		Make(window, out _).HandleMenuAction(Parse("""{"action":"open-recent","path":"C:\\proj"}"""));

		Assert.Equal("C:\\proj", window.OpenedPath);
	}

	[Fact]
	public void HandleMenuAction_OpenRecent_EmptyPath_DoesNotOpen() {
		var window = new FakeWindow();
		Make(window, out _).HandleMenuAction(Parse("""{"action":"open-recent","path":""}"""));

		Assert.Empty(window.Calls);
		Assert.Null(window.OpenedPath);
	}

	[Fact]
	public void HandleMenuAction_CloseWindow_UsesMenuClose() {
		var window = new FakeWindow();
		Make(window, out _).HandleMenuAction(Parse("""{"action":"close-window"}"""));

		Assert.Equal(["close-window"], window.Calls);
	}

	[Fact]
	public void HandleWindowControl_Close_UsesPlainClose() {
		var window = new FakeWindow();
		Make(window, out _).HandleWindowControl(Parse("""{"action":"close"}"""));

		Assert.Equal(["close"], window.Calls);
	}

	[Fact]
	public void HandleMenuAction_Exit_Quits() {
		var window = new FakeWindow();
		Make(window, out _).HandleMenuAction(Parse("""{"action":"exit"}"""));

		Assert.Equal(["quit"], window.Calls);
	}

	[Fact]
	public void PushFileIndex_PostsFileIndexMessage() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "");
		var controller = Make(new FakeWindow(), out var posted, fileSystem);

		controller.PushFileIndex();

		string message = Assert.Single(posted);
		Assert.Contains("\"type\":\"file-index\"", message);
	}

	[Fact]
	public void PushWindowState_PostsWindowStateMessage() {
		var controller = Make(new FakeWindow(), out var posted);

		controller.PushWindowState(maximized: true, focused: true);

		Assert.Contains("\"type\":\"window-state\"", Assert.Single(posted));
	}
}
