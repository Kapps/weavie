using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpControlBatchTests {
	private static AgentControlValue Value(string axis, string value) => new(axis, value);

	private static async Task ExpectStateAsync(AcpAgentSessionFixture fixture, string state) {
		fixture.Submit("control-state");
		await fixture.WaitForMessageAsync(message => message.Text == $"control state: {state}");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
	}

	[Fact]
	public async Task ABatchIsConfirmedInOrderAndNeverRemembered() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		await fixture.Session.ApplyControlsAsync([Value("model", "beta"), Value("fast", "true")]);

		Assert.Equal(["beta", "true"], fixture.Session.ControlState.Axes.Where(a => a.Id is "model" or "fast").Select(a => a.Value));
		await ExpectStateAsync(fixture, "beta/default/True");
		Assert.Empty(fixture.ControlDefaults.Resolve("fake"));
	}

	[Fact]
	public async Task EachChangeIsCheckedAgainstWhatTheAgentAdvertisesWhenItIsReached() {
		await using var fixture = AcpAgentSessionFixture.CreateModelEffortAdapter();
		await fixture.StartAsync();

		var early = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Session.ApplyControlsAsync([Value("effort", "high")]));
		await fixture.Session.ApplyControlsAsync([Value("model", "beta"), Value("effort", "high")]);

		Assert.Contains("doesn't advertise the 'effort' control; it advertises model, fast, mode", early.Message, StringComparison.Ordinal);
		await ExpectStateAsync(fixture, "beta/default/False/high");
	}

	[Fact]
	public async Task AnUnadvertisedValueFaultsTheBatchAloneAndAnEmptyOneWaitsForTheQueue() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		var error = await Assert.ThrowsAsync<AgentControlException>(() => fixture.Session.ApplyControlsAsync([Value("model", "gamma")]));
		fixture.Session.SetControl("model", "beta");
		await fixture.Session.ApplyControlsAsync([]);

		Assert.Equal("Fake ACP doesn't advertise 'gamma' for the 'model' control; it advertises alpha, beta.", error.Message);
		Assert.Equal("beta", Assert.Single(fixture.Session.ControlState.Axes, a => a.Id == "model").Value);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task ABatchQueuedBehindAPickerChangeLandsAfterIt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.SetControl("model", "beta");
		await fixture.Session.ApplyControlsAsync([Value("model", "alpha")]);

		await ExpectStateAsync(fixture, "alpha/default/False");
		Assert.Equal("beta", fixture.ControlDefaults.Resolve("fake")["model"]);
	}

	[Fact]
	public async Task ARestartBeforeTheAgentConfirmsFaultsTheBatchAndTheSuccessorOpensOnTheHeldValues() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.ControlHold.Held["model"] = "beta";
		var oldStarts = fixture.Events.Values.OfType<AgentSessionStarted>().ToHashSet(ReferenceEqualityComparer.Instance);

		var applying = fixture.Session.ApplyControlsAsync([Value("model", "beta")]);
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "control-started")));
		fixture.Session.Restart();

		var error = await Assert.ThrowsAsync<AgentControlException>(() => applying);
		Assert.Contains("replaced before the agent confirmed", error.Message, StringComparison.Ordinal);
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !oldStarts.Contains(started));
		await ExpectStateAsync(fixture, "beta/default/False");
		Assert.Empty(fixture.ControlDefaults.Resolve("fake"));
	}

	[Fact]
	public async Task ANoticeJoinsTheSavedTranscript() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();

		fixture.Session.Notice("Workflow: build runs on Beta");

		var notice = await fixture.WaitForMessageAsync(message => message.Type == "notice");
		Assert.Equal("Workflow: build runs on Beta", notice.Text);
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "notice" && message.Text == notice.Text);
	}
}
