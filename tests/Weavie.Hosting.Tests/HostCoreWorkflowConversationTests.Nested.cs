using System.Text.Json;
using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {
	private const string Group = """
		id: conversation
		description: A group forks main and each task forks the group.
		stages:
		  - id: group
		    context: fork
		    outputs: { note: "{{task.note}}" }
		    stages:
		      - id: plan
		        run: { prompt: plan the group }
		      - id: task
		        context: fork
		        run: { prompt: do the task }
		        outputs: { note: text }
		  - id: publish
		    run: { prompt: "publish {{group.note}}" }
		""";

	private const string Entries = """
		id: conversation
		description: One fresh conversation per entry.
		stages:
		  - id: list
		    run: { prompt: list the items }
		    outputs: { items: list }
		  - id: group
		    foreach: "{{list.items}}"
		    context: fresh
		    stages:
		      - id: first
		        run: { prompt: "first on {{group.item}}" }
		      - id: second
		        run: { prompt: "second on {{group.item}}" }
		  - id: publish
		    run: { prompt: publish }
		""";

	private const string StageDone = "mcp:completeStage:{\"outputs\":{},\"summary\":\"Done\"}";

	private static Task<JsonElement> StartedAsync(ConversationFixture fixture, string title) =>
		fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == title);

	[Fact]
	public async Task AGroupRunsItsStagesInItsForkAndATaskForksTheGroupsConversation() {
		await using var fixture = await ConversationFixture.StartAsync(Group);
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		string group = Conversation(await StartedAsync(fixture, "group"))!;
		await fixture.ReplyAsync(group, "echo: plan the group");
		Assert.Null(workflow.CaptureStageCall());
		fixture.Session.SendAgentPrompt(StageDone);
		await fixture.ReplyAsync(null, "completeStage failed: This conversation does not own an open workflow stage.");

		fixture.Send(group, StageDone);
		string task = Conversation(await StartedAsync(fixture, "group › task"))!;
		var reply = await fixture.ReplyAsync(task, "echo: do the task");
		string transcript = File.ReadAllText(Path.Combine(fixture.State("conversation-acp"), $"session-transcript-{Thread(reply)}.log"));
		Assert.Contains("plan the group", transcript, StringComparison.Ordinal);
		Assert.Contains("main context before review", transcript, StringComparison.Ordinal);

		fixture.Send(task, Completion);
		await fixture.ReplyAsync(null, "echo: Stage \"group\" ran in its own conversation and completed.");
		await fixture.ReplyAsync(null, "publish reviewed change");
		Assert.Equal("publish", workflow.Current!.Stage);
	}

	[Fact]
	public async Task EachEntryHasItsOwnConversationWhichClosesWhenTheEntryFinishes() {
		await using var fixture = await ConversationFixture.StartAsync(Entries);
		var workflow = fixture.Session.Workflow;
		workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "echo: list the items");
		fixture.Session.SendAgentPrompt("mcp:completeStage:{\"outputs\":{\"items\":[\"auth\",\"billing\"]},\"summary\":\"Listed\"}");

		string first = Conversation(await StartedAsync(fixture, "group 1"))!;
		var opened = await fixture.ReplyAsync(first, "echo: first on auth");
		fixture.Send(first, StageDone);
		var continued = await fixture.ReplyAsync(first, "echo: second on auth");
		Assert.Equal(Thread(opened), Thread(continued));
		Assert.Equal("group 1/2 › second", workflow.Current!.Path());
		fixture.Send(first, StageDone);
		await fixture.ClosedAsync(first);

		string second = Conversation(await StartedAsync(fixture, "group 2"))!;
		Assert.NotEqual(first, second);
		await fixture.ReplyAsync(second, "echo: first on billing");
		fixture.Send(second, StageDone);
		await fixture.ReplyAsync(second, "echo: second on billing");
		fixture.Send(second, StageDone);
		await fixture.ClosedAsync(second);
		await fixture.ReplyAsync(null, "echo: Stage \"group\" (entry 1 of 2: \"auth\") ran in its own conversation");
		Assert.Equal("publish", workflow.Current!.Stage);
	}

	[Fact]
	public async Task AGroupWhoseConversationIsLostPausesAndResumeOpensANewOne() {
		await using var fixture = await ConversationFixture.StartAsync(Entries);
		var workflow = fixture.Session.Workflow;
		var run = workflow.Start("conversation", Json("{}"));
		await fixture.ReplyAsync(null, "echo: list the items");
		fixture.Session.SendAgentPrompt("mcp:completeStage:{\"outputs\":{\"items\":[\"auth\"]},\"summary\":\"Listed\"}");
		string group = Conversation(await StartedAsync(fixture, "group 1"))!;
		await fixture.ReplyAsync(group, "echo: first on auth");
		await fixture.MessageAsync(message => Type(message) == "turn-completed" && Conversation(message) == group);

		await fixture.Session.Agent.Rewind!.RewindBeforeAsync("1");
		await Eventually(() => workflow.Current!.Phase == WorkflowPhase.Paused);
		workflow.Resume(run.Id);
		await Eventually(() => workflow.Current!.PausedReason?.Contains("Its stages' earlier work there is gone", StringComparison.Ordinal) == true);
		Assert.Equal("Stage \"group\" (entry 1 of 1: \"auth\") lost its conversation: it ended. Its stages' earlier work there is gone. Resume to continue in a new one.",
			workflow.Current!.PausedReason);

		workflow.Resume(run.Id);
		var renewed = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "group 1" && Conversation(message) != group);
		await fixture.ReplyAsync(Conversation(renewed), "echo: first on auth");
		Assert.Equal(WorkflowPhase.Open, workflow.Current!.Phase);
	}
}
