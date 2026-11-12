using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	private const string Loop = """
		id: conversation
		description: Fix, then review or recheck in the reviewer's conversation.
		stages:
		  - id: fix
		    run: { prompt: fix the change }
		    then:
		      ask: "Next?"
		      by: user
		      choices: { review: review, recheck: recheck, finish: done }
		  - id: review
		    run: { context: CONTEXT, prompt: review the change, outputs: { note: text } }
		    then: fix
		  - id: recheck
		    run: { context: { reuse: review }, prompt: recheck the change, outputs: { note: text } }
		    then: fix
		""";

	private const string MainDone = "mcp:completeStage:{\"outputs\":{},\"summary\":\"Fixed\"}";

	private static async Task<string> Fixed(ConversationFixture fixture, int fixes) {
		fixture.Session.SendAgentPrompt(MainDone);
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) is null
			&& Type(message) == "item-completed" && Text(message).Contains("now asks the user: Next?", StringComparison.Ordinal)) == fixes);
		return fixture.Session.Workflow.Current!.Id;
	}

	private static string Transcript(ConversationFixture fixture, JsonElementThread thread) =>
		File.ReadAllText(Path.Combine(fixture.State("conversation-acp"), $"session-transcript-{thread.Id}.log"));

	private sealed record JsonElementThread(string Id);

	[Fact]
	public async Task EveryReturnToAFreshStageAndEveryStageReusingItContinueOneConversation() {
		await using var fixture = await ConversationFixture.StartAsync(Loop.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "fix the change");
		workflow.Answer(await Fixed(fixture, 1), "review", "");
		string child = await fixture.WaitForReviewAsync();
		var thread = new JsonElementThread(Thread(await fixture.ReplyAsync(child, "echo: review the change")));
		Assert.DoesNotContain("opened again", Transcript(fixture, thread), StringComparison.Ordinal);
		fixture.Send(child, Completion);
		await fixture.ReplyAsync(child, "completeStage: Complete.");

		// Between visits the card takes replies, but nothing it says completes a stage.
		await fixture.ReplyAsync(null, "Summary: Checked concurrency");
		fixture.Send(child, Completion);
		await fixture.ReplyAsync(child, "completeStage failed: This conversation does not own an open workflow stage.");
		Assert.Equal("fix", workflow.Current!.Stage);

		workflow.Answer(await Fixed(fixture, 2), "review", "");
		await Eventually(() => Transcript(fixture, thread).Contains("has opened again; your earlier work on it is above.", StringComparison.Ordinal));
		fixture.Send(child, Completion);
		await Eventually(() => workflow.Current!.Stage == "fix");

		workflow.Answer(await Fixed(fixture, 3), "recheck", "");
		await Eventually(() => Transcript(fixture, thread).Contains("continues the conversation of stage", StringComparison.Ordinal));
		Assert.Equal(thread.Id, Thread(await fixture.ReplyAsync(child, "echo: recheck the change")));
		fixture.Send(child, Completion);
		await Eventually(() => workflow.Current!.Stage == "fix");
		Assert.Single(fixture.Messages, message => Type(message) == "side-conversation-started");
		Assert.DoesNotContain(fixture.Messages, message => Type(message) == "side-conversation-closed");

		workflow.Answer(await Fixed(fixture, 4), "finish", "");
		await fixture.ClosedAsync(child);
	}

	[Fact]
	public async Task AForkStageCopiesMainAgainOnEachVisitAndClosesItsEarlierCopy() {
		await using var fixture = await ConversationFixture.StartAsync(Loop.Replace("CONTEXT", "fork", StringComparison.Ordinal));
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "fix the change");
		workflow.Answer(await Fixed(fixture, 1), "review", "");
		string first = await fixture.WaitForReviewAsync();
		fixture.Send(first, Completion);
		await Eventually(() => workflow.Current!.Stage == "fix");
		workflow.Answer(await Fixed(fixture, 2), "review", "");
		await fixture.ClosedAsync(first);
		var second = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Conversation(message) != first);
		var reply = await fixture.ReplyAsync(Conversation(second), "echo: review the change");
		Assert.StartsWith("fake-fork-", Thread(reply), StringComparison.Ordinal);
		Assert.DoesNotContain("opened again", Transcript(fixture, new(Thread(reply))), StringComparison.Ordinal);
	}

	[Fact]
	public async Task AReusingStageThatArrivesFirstOpensTheConversationItsStageThenContinues() {
		await using var fixture = await ConversationFixture.StartAsync(Loop.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "fix the change");
		workflow.Answer(await Fixed(fixture, 1), "recheck", "");
		await fixture.MessageAsync(message => Conversation(message) is null && Type(message) == "notice"
			&& Text(message) == "Workflow: recheck opened a new conversation; stage \"review\" has no conversation to continue");
		var started = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "review");
		string child = Conversation(started)!;
		var thread = new JsonElementThread(Thread(await fixture.ReplyAsync(child, "echo: recheck the change")));
		fixture.Send(child, Completion);
		await Eventually(() => workflow.Current!.Stage == "fix");
		workflow.Answer(await Fixed(fixture, 2), "review", "");
		await Eventually(() => Transcript(fixture, thread).Contains("has opened again", StringComparison.Ordinal));
		Assert.Single(fixture.Messages, message => Type(message) == "side-conversation-started");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AnAgentRestartBetweenVisitsLeavesTheRunOpenAndTheNextVisitRestoresOrReplacesTheConversation(bool restoreFails) {
		await using var fixture = await ConversationFixture.StartAsync(Loop.Replace("CONTEXT", "fresh", StringComparison.Ordinal));
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "fix the change");
		workflow.Answer(await Fixed(fixture, 1), "review", "");
		string child = await fixture.WaitForReviewAsync();
		var thread = new JsonElementThread(Thread(await fixture.ReplyAsync(child, "echo: review the change")));
		fixture.Send(child, Completion);
		await Eventually(() => workflow.Current!.Stage == "fix");
		await fixture.ReplyAsync(null, "Summary: Checked concurrency");

		fixture.Session.RestartAgent();
		await Eventually(() => fixture.Session.Agent.Controls!.ControlState.Ready);
		Assert.Equal(WorkflowPhase.Open, workflow.Current!.Phase);
		if (restoreFails) File.WriteAllText(Path.Combine(fixture.State("conversation-acp"), "fail-restore"), "");
		workflow.Answer(await Fixed(fixture, 2), "review", "");
		if (restoreFails) {
			await fixture.MessageAsync(message => Conversation(message) is null && Type(message) == "notice"
				&& Text(message).StartsWith("Workflow: review opened a new conversation; its earlier one could not be restored:", StringComparison.Ordinal));
			await fixture.ClosedAsync(child);
			var replacement = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Conversation(message) != child);
			await fixture.ReplyAsync(Conversation(replacement), "echo: review the change");
		} else {
			await Eventually(() => Transcript(fixture, thread).Contains("has opened again", StringComparison.Ordinal));
			Assert.Equal(thread.Id, Thread(await fixture.ReplyAsync(child, "echo: review the change")));
		}
		Assert.Equal(WorkflowPhase.Open, workflow.Current!.Phase);
	}
}
