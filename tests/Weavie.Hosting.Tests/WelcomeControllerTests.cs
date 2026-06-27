using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The shared welcome flow drives the one welcome UI for every host: it injects the recents the page reads,
/// navigates to welcome.html, and routes the page's <c>menu-action</c> messages to the host's open handlers.
/// These pin that routing end-to-end over the bridge contract, no web view needed.
/// </summary>
public sealed class WelcomeControllerTests {
	[Fact]
	public async Task Show_InjectsRecents_ThenNavigates() {
		var (_, surface) = Wire(out _, out _, ["/a/one", "/b/two"]);
		await surface.Controller.ShowAsync();

		Assert.Equal("""window.__WEAVIE_WELCOME__ = {"recents":["/a/one","/b/two"]};""", surface.LastScript);
		Assert.Equal("app://app/welcome.html", surface.LastNavigated);
		Assert.True(surface.InjectedBeforeNavigate);
	}

	[Fact]
	public async Task OpenFolderMessage_InvokesOpenFolder() {
		var (bridge, surface) = Wire(out int[] folderOpens, out var openedRecent, []);
		await surface.Controller.ShowAsync();

		bridge.Receive("""{"type":"menu-action","action":"open-folder"}""");

		Assert.Equal(1, folderOpens[0]);
		Assert.Empty(openedRecent);
	}

	[Fact]
	public async Task OpenRecentMessage_InvokesOpenRecentWithPath() {
		var (bridge, surface) = Wire(out int[] folderOpens, out var openedRecent, []);
		await surface.Controller.ShowAsync();

		bridge.Receive("""{"type":"menu-action","action":"open-recent","path":"/proj/x"}""");

		Assert.Equal(["/proj/x"], openedRecent);
		Assert.Equal(0, folderOpens[0]);
	}

	[Fact]
	public async Task NonMenuActionAndEmptyRecentPath_AreIgnored() {
		var (bridge, surface) = Wire(out int[] folderOpens, out var openedRecent, []);
		await surface.Controller.ShowAsync();

		bridge.Receive("""{"type":"ready"}""");
		bridge.Receive("not json");
		bridge.Receive("""{"type":"menu-action","action":"open-recent"}"""); // no path

		Assert.Equal(0, folderOpens[0]);
		Assert.Empty(openedRecent);
	}

	[Fact]
	public async Task Detach_StopsRoutingMessages() {
		var (bridge, surface) = Wire(out int[] folderOpens, out _, []);
		await surface.Controller.ShowAsync();
		surface.Controller.Detach();

		bridge.Receive("""{"type":"menu-action","action":"open-folder"}""");

		Assert.Equal(0, folderOpens[0]);
	}

	[Fact]
	public async Task Refresh_ReinjectsLiveRecents() {
		var recents = new List<string> { "/gone" };
		var bridge = new FakeHostBridge();
		var surface = new FakeWebSurface();
		surface.Controller = new WelcomeController(
			bridge, surface, "app://app/welcome.html", () => recents, () => { }, _ => { });
		await surface.Controller.ShowAsync();

		recents.Clear(); // the host pruned the missing folder
		await surface.Controller.RefreshAsync();

		Assert.Equal("""window.__WEAVIE_WELCOME__ = {"recents":[]};""", surface.LastScript);
	}

	private static (FakeHostBridge bridge, FakeWebSurface surface) Wire(
		out int[] folderOpens, out List<string> openedRecent, IReadOnlyList<string> recents) {
		int[] fo = [0];
		var or = new List<string>();
		folderOpens = fo;
		openedRecent = or;
		var bridge = new FakeHostBridge();
		var surface = new FakeWebSurface();
		surface.Controller = new WelcomeController(
			bridge, surface, "app://app/welcome.html", () => recents, () => fo[0]++, or.Add);
		return (bridge, surface);
	}

	private sealed class FakeWebSurface : IWebSurface {
		public WelcomeController Controller { get; set; } = null!;
		public string? LastScript { get; private set; }
		public string? LastNavigated { get; private set; }
		public bool InjectedBeforeNavigate { get; private set; }

		public void Navigate(string url) => LastNavigated = url;

		public void RenderHtml(string html) { }

		public Task InjectStartupScriptAsync(string script) {
			LastScript = script;
			InjectedBeforeNavigate = LastNavigated is null;
			return Task.CompletedTask;
		}
	}
}
