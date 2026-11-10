using System.Collections.Concurrent;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPromptLifecycleTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompletionCallbackQueuesASuccessorWhoseCompletedToolKeepsTheTurnWorking(bool fromStatus) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-after-tool"), string.Empty);
		var statuses = new ConcurrentQueue<SessionStatus>();
		int submitted = 0;
		void SubmitSuccessor() {
			if (Interlocked.Exchange(ref submitted, 1) == 0) fixture.Submit("completed-tool-then-hold");
		}
		fixture.Events.Status.Changed += status => {
			statuses.Enqueue(status);
			if (fromStatus && status == SessionStatus.Idle) SubmitSuccessor();
		};
		fixture.Session.PaneMessage += message => {
			if (!fromStatus && message.Type == "turn-completed" && message.TurnId == "1") SubmitSuccessor();
		};

		fixture.Submit("first turn");
		await fixture.WaitForMessageAsync(message => message.Text == "Thinking after command");

		Assert.Equal(SessionStatus.Working, fixture.Events.Status.Status);
		Assert.Equal([SessionStatus.Working, SessionStatus.Idle, SessionStatus.Working], statuses.ToArray());
		Assert.Equal(
			["turn-started:1", "turn-completed:1", "turn-started:2"],
			fixture.Messages.Where(message => message.Type is "turn-started" or "turn-completed")
				.Select(message => $"{message.Type}:{message.TurnId}"));
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "turn-completed" && message.TurnId == "2");

		File.WriteAllText(Path.Combine(fixture.Workspace, "release-after-tool"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnrelatedToolActivityKeepsPendingUserInputWithoutStatusFlicker(bool input) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "hold-parallel-tools"), string.Empty);
		fixture.Submit(input ? "input-with-parallel-tool" : "permission-with-parallel-tool");
		var request = await fixture.WaitForMessageAsync(message => message.Type == (input ? "input-requested" : "approval-requested"));
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		var statuses = new ConcurrentQueue<SessionStatus>();
		fixture.Events.Status.Changed += statuses.Enqueue;

		File.WriteAllText(Path.Combine(fixture.Workspace, "release-parallel-tools"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Text == "Parallel command completed");
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		Assert.Empty(statuses);
		if (!input) {
			var status = new SessionStatusMachine();
			var activity = new SessionActivityTracker(status);
			foreach (var value in fixture.Events.Values) {
				status.Observe(value);
				activity.Observe(value);
			}
			Assert.Equal("needs approval: Protected operation", activity.Line);
			fixture.Session.ResolvePermission(request.RequestId!, "allow");
		} else {
			fixture.Session.ResolveInput(request.RequestId!, "accept", new Dictionary<string, IReadOnlyList<string>> { ["choice"] = ["one"] });
		}
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

}
