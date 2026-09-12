using System.Collections.Concurrent;
using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpCommandReadinessTests {
	[Fact]
	public async Task SideCommandsWaitForSessionSetupAfterCapabilitiesAreKnown() {
		await using var fixture = AcpAgentSessionFixture.CreateAgentAuthenticationAdapter();
		AssertLoading(fixture.Session.ControlState);
		fixture.Session.Start();
		var authentication = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");
		AssertLoading(fixture.Session.ControlState);

		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		var ready = await fixture.WaitForControlsAsync(state => state.Ready);
		Assert.Contains(ready.Slash, entry => entry.Id == AgentControlCommands.AskAside.Id);
	}

	[Fact]
	public async Task RestartPublishesLoadingControlsBeforeTheReplacementSessionIsReady() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		var updates = new ConcurrentQueue<AgentControlState>();
		fixture.Session.ControlStateChanged += updates.Enqueue;

		fixture.Session.Restart();

		Assert.True(updates.TryPeek(out var restarting));
		AssertLoading(Assert.IsType<AgentControlState>(restarting));
		await fixture.WaitForControlsAsync(state => state.Ready);
		Assert.True(fixture.Session.ControlState.Ready);
	}

	[Fact]
	public async Task RuntimeFailureRevokesSideCommandsAndOrdinaryInputStillRestartsTheSession() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("malformed-update");
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		AssertLoading(await fixture.WaitForControlsAsync(state => !state.Ready));

		fixture.Submit("continue");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: continue");
		Assert.True(fixture.Session.ControlState.Ready);
	}

	private static void AssertLoading(AgentControlState controls) {
		Assert.False(controls.Ready);
		Assert.Contains(controls.Slash, entry => entry.Id == AgentControlCommands.ClearConversation.Id);
		Assert.DoesNotContain(controls.Slash, entry => entry.Id == AgentControlCommands.AskAside.Id);
	}
}
