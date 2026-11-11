using Weavie.Core.Agents;
using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	[Theory]
	[InlineData("fresh")]
	[InlineData("fork")]
	public async Task RevisionsAndFailureResumeStayInTheOwnedConversationAndReportsGoToMain(string context) {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", context, StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		var session = fixture.Session;
		string runId = session.Workflow.Current!.Id;
		var execution = session.Workflow.Execution;
		var initial = await fixture.ReplyAsync(child, "echo: review the change");
		Assert.Equal(context == "fork", Thread(initial).StartsWith("fake-fork-", StringComparison.Ordinal));
		string transcript = File.ReadAllText(Path.Combine(fixture.State("conversation-acp"), $"session-transcript-{Thread(initial)}.log"));
		Assert.Equal(context == "fork", transcript.Contains("main context before review", StringComparison.Ordinal));
		Assert.DoesNotContain(fixture.Messages, message => Conversation(message) is null && Text(message) == "echo: review the change");

		fixture.Send(child, Completion);
		await fixture.ReplyAsync(child, "completeStage: Complete.");
		await fixture.ReplyAsync(null, "now asks the user: Accept reviewed change?");
		Assert.Equal(WorkflowPhase.Asking, session.Workflow.Current!.Phase);
		Assert.Same(execution, session.Workflow.Execution);
		Assert.DoesNotContain(fixture.Messages, message => Type(message) == "side-conversation-closed" && Conversation(message) == child);
		var report = await fixture.ReplyAsync(null, "Summary: Checked concurrency");
		Assert.Contains("note = reviewed change", Text(report), StringComparison.Ordinal);

		session.Workflow.Answer(runId, "revise", "Include cancellation");
		var revision = await fixture.ReplyAsync(child, "Feedback:");
		Assert.Contains("Include cancellation", Text(revision), StringComparison.Ordinal);
		Assert.Equal(Thread(initial), Thread(revision));
		fixture.Send(child, "mcp:failStage:{\"reason\":\"Need more evidence\"}");
		await fixture.ReplyAsync(child, "failStage: The run is paused");
		Assert.Equal("The agent couldn't finish stage \"review\": Need more evidence", session.Workflow.Current!.PausedReason);
		session.Workflow.Resume(runId);
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) == child
			&& Type(message) == "item-completed" && Text(message).StartsWith("echo: review the change", StringComparison.Ordinal)) == 2);
		Assert.Same(execution, session.Workflow.Execution);
		Assert.Single(fixture.Messages, message => Type(message) == "side-conversation-started" && Text(message) == "review");

		fixture.Send(child, Completion);
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) is null
			&& Type(message) == "item-completed" && Text(message).Contains("now asks the user: Accept reviewed change?", StringComparison.Ordinal)) == 2);
		session.Workflow.Answer(runId, "approve", "");
		await fixture.ClosedAsync(child);
		await fixture.ReplyAsync(null, "echo: publish reviewed change");
		Assert.Throws<InvalidOperationException>(() => fixture.Send(child, "stale stage input"));
		session.SendAgentPrompt("mcp:completeStage:{\"outputs\":{},\"summary\":\"Published\"}");
		await fixture.ReplyAsync(null, "completeStage: Complete.");
		Assert.Equal(WorkflowPhase.Done, session.Workflow.Current!.Phase);
	}

	[Theory]
	[InlineData("fresh")]
	[InlineData("fork")]
	public async Task MainAndBtwCannotCompleteTheOwnedStage(string context) {
		await using var fixture = await ConversationFixture.StartAsync(Review.Replace("CONTEXT", context, StringComparison.Ordinal));
		string child = await fixture.StartReviewAsync();
		fixture.Session.SendAgentPrompt(Completion);
		var mainReply = await fixture.ReplyAsync(null, "completeStage");
		Assert.Equal("completeStage failed: This conversation does not own an open workflow stage.", Text(mainReply));
		fixture.Session.Agent.SideConversations!.AskAside(AgentTurnSubmission.Prompt(Completion));
		var aside = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == Completion);
		await fixture.ReplyAsync(Conversation(aside), "completeStage failed: This conversation does not own an open workflow stage.");
		Assert.Equal(WorkflowPhase.Open, fixture.Session.Workflow.Current!.Phase);
		Assert.Empty(fixture.Session.Workflow.Current.Outputs);
		fixture.Send(child, Completion);
		await fixture.ReplyAsync(child, "completeStage: Complete.");
		Assert.Equal(WorkflowPhase.Asking, fixture.Session.Workflow.Current!.Phase);
	}

}
