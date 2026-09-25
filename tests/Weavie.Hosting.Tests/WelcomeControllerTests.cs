using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Hosting.Messaging;
using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The shared welcome flow drives the one welcome UI for every host: it injects the recents and app-wide page
/// globals, navigates to welcome.html, routes <c>window.menu</c> events to the host's open handlers, and serves the
/// app-wide features Getting Started needs with no workspace open. Pinned over the bridge contract, no web view.
/// </summary>
public sealed class WelcomeControllerTests : IDisposable {
	private readonly TempDirectory _temp = new("weavie-welcome");
	private readonly HostServices _services;
	private readonly FakeHostBridge _bridge = new();
	private readonly FakeWebSurface _surface = new();
	private readonly List<string> _recents = ["/a/one", "/b/two"];
	private readonly List<string> _opened = [];
	private readonly WelcomeController _controller;
	private int _folderOpens;

	public WelcomeControllerTests() {
		LoginShellEnvironment.MarkImported();
		_services = TestHost.IsolatedServices(_temp.Path);
		_controller = new WelcomeController(
			_bridge, _surface, new InlineUiDispatcher(), _services, "app://app/welcome.html", () => _recents,
			() => _folderOpens++, _opened.Add);
	}

	[Fact]
	public async Task Show_InjectsWelcomeConfigAndPageGlobals_ThenNavigates() {
		await _controller.ShowAsync();

		Assert.StartsWith(
			"""window.__WEAVIE_WELCOME__ = {"recents":["/a/one","/b/two"],"setupCompleted":true};""",
			_surface.LastScript,
			StringComparison.Ordinal);
		foreach (string global in new[] { "__WEAVIE_AGENT__", "__WEAVIE_THEME__", "__WEAVIE_COMMANDS__", "__WEAVIE_KEYBINDINGS__" }) {
			Assert.Contains($"window.{global} = ", _surface.LastScript, StringComparison.Ordinal);
		}

		Assert.Equal("app://app/welcome.html", _surface.LastNavigated);
	}

	[Fact]
	public async Task MenuMessages_RouteToTheOpenHandlers_IgnoringMalformedOnes() {
		await _controller.ShowAsync();

		_bridge.Receive("not json");
		_bridge.Receive(HostEvent("window", "menu", """{"action":"open-recent"}""")); // no path
		_bridge.Receive(HostEvent("window", "menu", """{"action":"open-folder"}"""));
		_bridge.Receive(HostEvent("window", "menu", """{"action":"open-recent","path":"/proj/x"}"""));

		await Wait.UntilAsync(() => _opened.Count == 1);
		Assert.Equal(["/proj/x"], _opened);
		Assert.Equal(1, _folderOpens);
	}

	[Fact]
	public async Task Detach_StopsServingThePage() {
		await _controller.ShowAsync();
		Assert.True(_bridge.HasMessageReceiver);

		_controller.Detach();

		Assert.False(_bridge.HasMessageReceiver);
	}

	[Fact]
	public async Task Refresh_ReinjectsLiveRecentsAndSetupState() {
		await _controller.ShowAsync();
		_recents.Clear(); // the host pruned the missing folder
		_services.Settings.Set(CoreSettings.GettingStartedCompleted, JsonSerializer.SerializeToElement(false));

		await _controller.RefreshAsync();

		Assert.StartsWith(
			"""window.__WEAVIE_WELCOME__ = {"recents":[],"setupCompleted":false};""",
			_surface.LastScript,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task SettingsSet_WritesTheGlobalSettingAndPushesTheTheme() {
		await _controller.ShowAsync();

		var response = await RequestAsync("settings", "set", """{"key":"theme.mode","value":"light"}""");

		Assert.Null(response.Error);
		Assert.Equal("light", _services.Settings.RequireString(ThemeSettings.ModeKey));
		Assert.Equal("light", _bridge.LastEvent("settings", "theme")?.GetProperty("mode").GetString());
		var read = await RequestAsync("settings", "get", """{"key":"theme.mode"}""");
		Assert.Equal("light", read.Payload.GetProperty("value").GetString());
	}

	[Fact]
	public async Task SettingsSet_RejectsAnInvalidValue() {
		await _controller.ShowAsync();

		var response = await RequestAsync("settings", "set", """{"key":"theme.mode","value":"sepia"}""");

		Assert.NotNull(response.Error);
		Assert.Equal("system", _services.Settings.RequireString(ThemeSettings.ModeKey));
	}

	[Fact]
	public async Task AgentDefaults_ReportsClaudeUnavailable_WhenItsPathDoesNotResolve() {
		_services.Settings.Set(CoreSettings.ClaudePath, JsonSerializer.SerializeToElement(_temp.Combine("missing-claude")));
		await _controller.ShowAsync();

		var defaults = await RequestAsync("agentDefaults", "get", "{}");

		var claude = defaults.Payload.GetProperty("providers").EnumerateArray()
			.Single(provider => provider.GetProperty("id").GetString() == "claude");
		Assert.False(claude.GetProperty("available").GetBoolean());
		Assert.Contains("missing-claude", claude.GetProperty("unavailableReason").GetString(), StringComparison.Ordinal);
	}

	private async Task<MessageEnvelope> RequestAsync(string feature, string name, string payload) {
		string id = Guid.NewGuid().ToString("n");
		_bridge.Receive(
			$$"""{"scope":"host","session":null,"kind":"request","requestId":"{{id}}","feature":"{{feature}}","name":"{{name}}","payload":{{payload}},"error":null}""");
		return await Wait.ForReferenceAsync(() => _bridge.Sent
			.Select(sent => MessageEnvelope.TryParse(sent.Json, out var envelope) ? envelope : null)
			.FirstOrDefault(envelope => envelope is { Kind: MessageKind.Response } && envelope.RequestId == id));
	}

	private static string HostEvent(string feature, string name, string payload) =>
		$$"""{"scope":"host","session":null,"kind":"event","requestId":null,"feature":"{{feature}}","name":"{{name}}","payload":{{payload}},"error":null}""";

	public void Dispose() {
		_controller.Detach();
		_services.Keybindings.Dispose();
		_services.Settings.Dispose();
		_temp.Dispose();
	}

	private sealed class FakeWebSurface : IWebSurface {
		public string LastScript { get; private set; } = string.Empty;
		public string? LastNavigated { get; private set; }

		public void RenderHtml(string html) { }

		public Task LoadAsync(string url, string script) {
			LastNavigated = url;
			LastScript = script;
			return Task.CompletedTask;
		}
	}
}
