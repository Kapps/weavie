using Weavie.Core.Agents;
using Weavie.Core.Processes;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Hosting.Tests;

// /clear replaces the conversation on the running process when the agent can close sessions; whatever the retired
// conversation still had in flight must never reach its successor.
public sealed class AcpLiveReplacementTests {
	[Theory]
	[InlineData("prompt")]
	[InlineData("permission")]
	[InlineData("elicitation")]
	[InlineData("control")]
	[InlineData("terminal")]
	[InlineData("steering")]
	[InlineData("late-update")]
	public async Task ClearKeepsTheRetiredConversationsWorkAwayFromItsSuccessor(string work) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		await StartWorkAsync(fixture, work);

		fixture.Session.StartNewConversation();
		int reset = ResetIndex(fixture);
		foreach (string held in new[] { "hold", "control", "steering" }) Signal(fixture, "release-" + held);
		await WaitForCloseAsync(fixture, "fake-session");
		if (work == "terminal") Assert.Equal("Request cancelled.", await WaitForFileAsync(fixture.Workspace, "terminal-wait-answered", string.Empty));

		await AssertSuccessorAnswersAsync(fixture, "fake-session-2");
		AssertNothingFromAfterReset(fixture, reset, "fake-session");
		Assert.DoesNotContain(fixture.Messages, message => message.Text == "late after close");
		if (work == "control") {
			fixture.Submit("control-state");
			await fixture.WaitForMessageAsync(message => message.Text == "control state: alpha/default/False");
		}
		Assert.Equal(1, ProcessStarts(fixture));
	}

	// The opening may never return and holds the agent's only opening slot, so only a restart escapes it.
	[Fact]
	public async Task ClearDuringTheOpeningRestartsTheProcess() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		Signal(fixture, "hold-open");
		fixture.Start();
		await WaitForFileAsync(fixture.Workspace, "open-started", string.Empty);

		fixture.Session.StartNewConversation();
		int reset = ResetIndex(fixture);
		Signal(fixture, "release-open");

		await fixture.WaitForControlsAsync(state => state.Ready);
		await AssertSuccessorAnswersAsync(fixture, "fake-session-2");
		AssertNothingFromAfterReset(fixture, reset, "fake-session");
		Assert.Equal(2, ProcessStarts(fixture));
	}

	[Fact]
	public async Task ClearDuringATerminalLoginNeverLetsTheLoginRestartTheSuccessor() {
		await using var fixture = AcpAgentSessionFixture.CreateCrashingTerminalAuthenticationAdapter(out var terminal);
		fixture.Start();
		var login = await fixture.WaitForMessageAsync(message => message.Type == "authentication-requested");
		fixture.Session.Authenticate(login.RequestId!, "fake-terminal-login", new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await terminal.Started.WaitAsync(TimeSpan.FromSeconds(10));

		fixture.Session.StartNewConversation();
		int reset = ResetIndex(fixture);
		terminal.Release();
		var successor = await fixture.WaitForMessageAsync(message =>
			message.Type == "authentication-requested" && message.RequestId != login.RequestId);

		Assert.Contains(fixture.Messages.Take(reset), message =>
			message.Type == "authentication-resolved" && message.RequestId == login.RequestId && message.Status == "cancelled");
		Assert.DoesNotContain(fixture.Messages.Skip(reset), message =>
			message.RequestId == login.RequestId || message.Type == "error");
		Assert.Equal("authentication:2", successor.RequestId);
		Assert.Equal(1, ProcessStarts(fixture));
	}

	[Fact]
	public async Task ClearRestartsAnAgentThatCannotCloseSessions() {
		await using var fixture = AcpAgentSessionFixture.CreateMinimalCapabilitiesAdapter();
		await fixture.StartAsync();
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		fixture.Session.StartNewConversation();
		await fixture.WaitForControlsAsync(state => !state.Ready);
		await fixture.WaitForControlsAsync(state => state.Ready);

		Assert.Equal(2, ProcessStarts(fixture));
		Assert.False(File.Exists(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log")));
	}

	private static async Task StartWorkAsync(AcpAgentSessionFixture fixture, string work) {
		switch (work) {
			case "prompt":
				fixture.Submit("hold");
				await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold");
				break;
			case "permission":
				fixture.Submit("permission");
				await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
				break;
			case "elicitation":
				fixture.Submit("input");
				await fixture.WaitForMessageAsync(message => message.Type == "input-requested");
				break;
			case "control":
				Signal(fixture, "hold-control");
				fixture.Session.SetControl("model", "beta");
				await WaitForFileAsync(fixture.Workspace, "control-started", string.Empty);
				break;
			case "terminal":
				fixture.Submit("terminal-wait");
				await WaitForFileAsync(fixture.Workspace, "terminal-wait-started", string.Empty);
				break;
			case "steering":
				fixture.Submit("hold");
				await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold");
				fixture.Submit("held-steering");
				await WaitForFileAsync(fixture.Workspace, "steering-started", string.Empty);
				break;
			case "late-update":
				fixture.Submit("hello");
				await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
				Signal(fixture, "late-after-close");
				break;
		}
	}

	internal static async Task AssertSuccessorAnswersAsync(AcpAgentSessionFixture fixture, string sessionId) {
		fixture.Submit("identify-session");
		await fixture.WaitForMessageAsync(message => message.Text == "session: " + sessionId);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ThreadId == sessionId);
		Assert.Equal(sessionId, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	internal static void AssertNothingFromAfterReset(AcpAgentSessionFixture fixture, int reset, string retiredSessionId) {
		Assert.DoesNotContain(fixture.Messages.Skip(reset), message => message.ThreadId == retiredSessionId || message.Type == "error");
		Assert.DoesNotContain(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.ThreadId == retiredSessionId);
		Assert.Empty(fixture.Session.QueuedSubmissions);
	}

	internal static int ProcessStarts(AcpAgentSessionFixture fixture) =>
		fixture.Events.Values.Count(value => value is AgentProcessChanged { Change.State: SupervisorState.Running });

	internal static void Signal(AcpAgentSessionFixture fixture, string name) => File.WriteAllText(Path.Combine(fixture.Workspace, name), string.Empty);

	// Waits for the fake agent to write a file containing <paramref name="expected"/>, returning its contents.
	internal static async Task<string> WaitForFileAsync(string directory, string name, string expected) {
		string path = Path.Combine(directory, name);
		return await Wait.ForReferenceAsync(() =>
			File.Exists(path) && File.ReadAllText(path) is var text && text.Contains(expected, StringComparison.Ordinal) ? text : null);
	}

	// Waits for the fake agent to close exactly <paramref name="sessionId"/>.
	internal static Task WaitForCloseAsync(AcpAgentSessionFixture fixture, string sessionId) =>
		Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log"))
			&& File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "closes.log")).Contains(sessionId));

	private static int ResetIndex(AcpAgentSessionFixture fixture) =>
		fixture.Messages.ToList().FindLastIndex(message => message.Type == "transcript-reset");
}
