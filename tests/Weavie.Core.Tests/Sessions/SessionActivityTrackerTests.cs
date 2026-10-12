using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Exercises <see cref="SessionActivityTracker"/>'s line and change notifications.</summary>
public sealed class SessionActivityTrackerTests {
	private static readonly AgentActivity Test = new(AgentActivityKind.Execute, "pnpm test");
	private static readonly AgentActivity Edit = new(AgentActivityKind.Edit, "App.tsx");

	private readonly SessionStatusMachine _status = new();
	private readonly SessionActivityTracker _tracker;
	private int _changes;

	public SessionActivityTrackerTests() {
		_tracker = new SessionActivityTracker(_status);
		_tracker.Changed += () => _changes++;
	}

	private void Observe(AgentEvent value) {
		_status.Observe(value);
		_tracker.Observe(value);
	}

	[Fact]
	public void WorkingShowsTheLatestTool() {
		Observe(new AgentPromptSubmitted(null, "go"));
		Observe(new AgentActivityObserved(Test, AwaitingApproval: false));

		Assert.Equal("running pnpm test", _tracker.Line);
	}

	[Fact]
	public void NeedsInputShowsThePendingApproval_AndApprovalReturnsToTheTool() {
		Observe(new AgentPromptSubmitted(null, "go"));
		Observe(new AgentActivityObserved(Test, AwaitingApproval: false));
		Observe(new AgentActivityObserved(Test, AwaitingApproval: true));
		Observe(new AgentPermissionResolved(RequiresUserInput: true));

		Assert.Equal("wants to run pnpm test", _tracker.Line);

		_status.ObserveUserInput([(byte)'y']);
		Assert.Equal("running pnpm test", _tracker.Line);
	}

	[Fact]
	public void SubagentApprovalCountsButOnlyTheMainTurnClears() {
		Observe(new AgentPromptSubmitted(null, "go"));
		Observe(new AgentConversationEvent("sub", new AgentActivityObserved(Edit, AwaitingApproval: false)));
		Observe(new AgentConversationEvent("sub", new AgentTurnStopped(WillResume: false)));
		Assert.Equal("editing App.tsx", _tracker.Line);

		Observe(new AgentTurnStopped(WillResume: false));
		Assert.Null(_tracker.Line);
	}

	[Fact]
	public void RepeatedActivityRaisesOneChange() {
		Observe(new AgentActivityObserved(Test, AwaitingApproval: false));
		Observe(new AgentActivityObserved(Test, AwaitingApproval: false));

		Assert.Equal(1, _changes);
	}

	[Theory]
	[InlineData(SessionStatus.Starting, SessionStatus.Idle, false)]
	[InlineData(SessionStatus.Idle, SessionStatus.Working, true)]
	[InlineData(SessionStatus.Working, SessionStatus.Idle, true)]
	[InlineData(SessionStatus.Idle, SessionStatus.Waiting, false)]
	public void LastActiveMarksStartAndFinishOfWork(SessionStatus prior, SessionStatus next, bool marks) =>
		Assert.Equal(marks, SessionStatusMachine.MarksLastActive(prior, next));
}
