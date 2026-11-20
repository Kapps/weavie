using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

// Theme tools, key bindings and the client commands an agent runs follow the session's device, like its settings.
public sealed partial class HostCoreDeviceTests {
	private static readonly ThemeRequest Darken = new(
		"applyThemeTransform", JsonSerializer.SerializeToElement(new { op = "darken", amount = 0.6, target = "colors" }));

	private static void Show(TestHost host, WebPeer page, string polarity) => Send(host, page, MessageEnvelope.Event(
		MessageScope.Host, null, "device", "showing", JsonSerializer.SerializeToElement(new { polarity })));

	[Fact]
	public async Task ThemeTools_OnASessionTheLaptopClaimed_GoToTheLaptop() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var reply = await host.WorkspaceSession.DeviceForTest.ThemesAsync(Darken, CancellationToken.None);

		Assert.Equal("Set on laptop.", reply.Text);
		var (page, request) = Assert.Single(Asked(host, "themes"));
		Assert.Equal(Visitor, page);
		Assert.Equal(("applyThemeTransform", "darken"), (request.GetProperty("tool").GetString(), request.GetProperty("args").GetProperty("op").GetString()));
		Assert.Empty(host.ThemeOverrides.Get(ThemeSettings.DefaultDarkThemeId));
	}

	[Fact]
	public async Task ThemeTools_OnThisHostsDevice_EditTheThemeTheLatestPageShows() {
		await using var host = await TestHost.StartAsync();
		var page = new WebPeer(TestHost.TestPageId);
		Show(host, page, "light");

		var reply = await host.WorkspaceSession.DeviceForTest.ThemesAsync(Darken, CancellationToken.None);

		Assert.Contains($"to theme '{ThemeSettings.DefaultLightThemeId}' on this device (", reply.Text, StringComparison.Ordinal);
		Assert.Single(host.ThemeOverrides.Get(ThemeSettings.DefaultLightThemeId));
		Assert.Empty(Asked(host, "themes"));

		host.Bridge.Disconnect(page);
		host.DrainMessages();
		var unknown = await host.WorkspaceSession.DeviceForTest.ThemesAsync(Darken, CancellationToken.None);
		Assert.True(unknown.IsError);
		Assert.Matches("^No page from .+ is open", unknown.Text);
	}

	[Fact]
	public async Task KeyBindings_OnASessionTheLaptopClaimed_ComeFromTheLaptop() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.RequestResponder = envelope => envelope is { Feature: "device", Name: "keybindings" }
			? new FakeWebResponse(JsonSerializer.SerializeToElement(new[] { new { key = "ctrl+9", command = CoreCommands.IncreaseFontSize } }), null)
			: null;
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var bindings = await host.WorkspaceSession.DeviceForTest.KeybindingsAsync(CancellationToken.None);

		Assert.Equal(("ctrl+9", CoreCommands.IncreaseFontSize), (Assert.Single(bindings).Key, bindings[0].Command));
		Assert.Equal(Visitor, Assert.Single(Asked(host, "keybindings")).Page);
	}

	[Fact]
	public async Task OnlyAPageThisHostServes_SaysWhichThemeIsOnScreen() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);
		Show(host, new WebPeer(TestHost.TestPageId), "dark");

		Show(host, Visitor, "light");

		var reply = await host.WorkspaceSession.DeviceForTest.ThemesAsync(Darken, CancellationToken.None);
		Assert.Contains($"'{ThemeSettings.DefaultDarkThemeId}'", reply.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AClientCommandAnAgentRuns_GoesToTheDevice_WithNoViewAttached() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);
		long fontSize = host.Settings.RequireInt("font.size");

		var execution = await host.WorkspaceSession.Commands.PrepareModelInvocationAsync(CoreCommands.IncreaseFontSize, null, CancellationToken.None);

		Assert.Equal("Ran on laptop.", execution.Result.Message);
		var (page, request) = Assert.Single(Asked(host, "command"));
		Assert.Equal((Visitor, CoreCommands.IncreaseFontSize, host.WorkspaceSession.Address.Slot), (
			page, request.GetProperty("id").GetString(), request.GetProperty("session").GetProperty("slot").GetString()));
		Assert.Equal(fontSize, host.Settings.RequireInt("font.size"));
	}

	[Fact]
	public async Task AClientCommandAnAgentRuns_FailsNamingTheDevice_WhenNoPageCanRunIt() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Disconnect(new WebPeer(TestHost.TestPageId));
		host.DrainMessages();

		var execution = await host.WorkspaceSession.Commands.PrepareModelInvocationAsync(CoreCommands.SelectTheme, """{"id":"weavie-light"}""", CancellationToken.None);

		Assert.False(execution.Result.Ok);
		Assert.Matches("^No page from .+ is open", execution.Result.Error);
	}

	[Fact]
	public async Task AClientCommandWithNoDeadline_OutlivesTheOperationDeadline() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var pending = host.WorkspaceSession.Commands.PrepareModelInvocationAsync(CoreCommands.InstallThemeFromFile, null, CancellationToken.None);
		host.Time.Advance(TimeSpan.FromSeconds(MessageSettings.DefaultOperationDeadlineSeconds * 2));
		host.DrainMessages();

		Assert.False(pending.IsCompleted);
		host.Bridge.Disconnect(Visitor);
		host.DrainMessages();
		Assert.Contains("laptop disconnected", (await pending).Result.Error, StringComparison.Ordinal);
	}
}
