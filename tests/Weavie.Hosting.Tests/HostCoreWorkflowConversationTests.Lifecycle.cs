using Weavie.Core.Commands;
using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RewindClosesAnIdleStageAndPausesIt(bool laterTurn) {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		await fixture.MessageAsync(message => Type(message) == "turn-completed" && Conversation(message) == child);
		if (laterTurn) {
			fixture.Session.SendAgentPrompt("main second turn");
			await fixture.ReplyAsync(null, "echo: main second turn");
			await fixture.MessageAsync(message => Type(message) == "turn-completed" && Conversation(message) is null && message.GetProperty("turnId").GetString() == "2");
		}
		await fixture.Session.Agent.Rewind!.RewindBeforeAsync(laterTurn ? "2" : "1");
		await Eventually(() => fixture.Session.Workflow.Current!.Phase == WorkflowPhase.Paused);
		Assert.Throws<InvalidOperationException>(() => fixture.Send(child, "closed stage input"));
	}

	[Fact]
	public async Task ProcessRestartPausesTheLostStageAndResumeCreatesANewConversation() {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		string runId = fixture.Session.Workflow.Current!.Id;
		fixture.Session.RestartAgent();
		await Eventually(() => fixture.Session.Workflow.Current!.Phase == WorkflowPhase.Paused);
		Assert.Contains("Resume to open a new conversation", fixture.Session.Workflow.Current!.PausedReason, StringComparison.Ordinal);
		fixture.Session.SendAgentPrompt("main after restart");
		await fixture.ReplyAsync(null, "echo: main after restart");
		fixture.Session.Workflow.Resume(runId);
		var resumed = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "review" && Conversation(message) != child);
		await fixture.ReplyAsync(Conversation(resumed), "echo: review the change");
		Assert.Throws<InvalidOperationException>(() => fixture.Send(child, "lost stage input"));
		fixture.Send(Conversation(resumed)!, Completion);
		await fixture.ReplyAsync(Conversation(resumed), "completeStage: Complete.");
		Assert.Equal(WorkflowPhase.Asking, fixture.Session.Workflow.Current!.Phase);
	}

	[Fact]
	public async Task UnloadDoesNotLeaveAReloadedStageWaitingForAConversationThatNoLongerExists() {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		string runId = fixture.Session.Workflow.Current!.Id;
		Assert.True((await fixture.Host.UnloadSessionAsync("conversation")).Ok);
		Assert.True((await fixture.Host.InvokeCommandAsync(fixture.Host.WorkspaceSession.SlotId, SessionCommands.LoadSession,
			new { id = "conversation" }, CancellationToken.None)).Ok);
		await Eventually(() => fixture.Host.Core.SessionForTest("conversation") is not null);
		Assert.Equal((runId, WorkflowPhase.Paused), (fixture.Session.Workflow.Current!.Id, fixture.Session.Workflow.Current.Phase));
		await Eventually(() => fixture.Session.Agent.Controls!.ControlState.Ready);
		fixture.Session.Workflow.Resume(runId);
		var resumed = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "review" && Conversation(message) != child);
		await fixture.ReplyAsync(Conversation(resumed), "echo: review the change");
		fixture.Send(Conversation(resumed)!, Completion);
		await fixture.ReplyAsync(Conversation(resumed), "completeStage: Complete.");
		Assert.Equal(WorkflowPhase.Asking, fixture.Session.Workflow.Current!.Phase);
	}
}
