using System.Text.Json;
using Weavie.Workflows;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	private static readonly WorkflowStartPrompt ImagePrompt = new("review the change", [new(".png", "image/png", [0x89, 0x50, 0x4e, 0x47])]);
	private static string ImageFlow(string context) => Review.Replace("CONTEXT", context, StringComparison.Ordinal)
		.Replace("prompt: review the change", "prompt: \"{{run.prompt}}\"", StringComparison.Ordinal)
		.Replace("publish {{review.note}}", "publish {{run.prompt}}", StringComparison.Ordinal);

	[Theory]
	[InlineData("fresh")]
	[InlineData("fork")]
	public async Task PromptImagesBelongToEachChildAndMainRatherThanTheRun(string context) {
		await using var fixture = await ConversationFixture.StartAsync(ImageFlow(context));
		string child = await fixture.StartReviewAsync(ImagePrompt);
		var workflow = fixture.Session.Workflow;
		string runId = workflow.Current!.Id;
		Assert.Single(fixture.Messages, message => Type(message) == "user-image" && Conversation(message) == child);
		Assert.False(workflow.Current.PromptImagesSent);

		fixture.Send(child, Completion);
		await fixture.ReplyAsync(null, "now asks the user: Accept reviewed change?");
		workflow.Answer(runId, "revise", "Retain the image");
		await fixture.ReplyAsync(child, "Feedback:");
		workflow.Execution!.Capture().Fail("Need more evidence");
		workflow.Resume(runId);
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) == child
			&& Type(message) == "item-completed" && Text(message) == "echo: review the change") == 2);
		Assert.Single(fixture.Messages, message => Type(message) == "user-image" && Conversation(message) == child);

		fixture.Send(child, Completion);
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) is null
			&& Type(message) == "item-completed" && Text(message).Contains("now asks the user: Accept reviewed change?", StringComparison.Ordinal)) == 2);
		workflow.Answer(runId, "approve", "");
		await fixture.ReplyAsync(null, "echo: publish review the change");
		Assert.Single(fixture.Messages, message => Type(message) == "user-image" && Conversation(message) is null);
		Assert.True(workflow.Current.PromptImagesSent);

		// A fresh stage continues its conversation, which already has the images; a fork copies main again and sends them anew.
		workflow.Jump(runId, "review");
		string? visited = context == "fresh" ? child
			: Conversation(await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Conversation(message) != child));
		await Eventually(() => fixture.Messages.Count(message => Conversation(message) == visited
			&& Type(message) == "item-completed" && Text(message) == "echo: review the change") == (context == "fresh" ? 3 : 1));
		Assert.Single(fixture.Messages, message => Type(message) == "user-image" && Conversation(message) == visited);
		Assert.True(workflow.Current.PromptImagesSent);
	}

	[Fact]
	public async Task ARestoredChildKeepsTheImagesItReceivedBeforeAnAgentRestart() {
		await using var fixture = await ConversationFixture.StartAsync(ImageFlow("fresh"));
		string child = await fixture.StartReviewAsync(ImagePrompt);
		var workflow = fixture.Session.Workflow;
		var run = workflow.Current!;
		fixture.Send(child, Completion);
		await fixture.ReplyAsync(null, "now asks the user: Accept reviewed change?");
		fixture.Session.RestartAgent();
		await Eventually(() => workflow.Current!.Phase == WorkflowPhase.Paused);
		fixture.Session.SendAgentPrompt("main after restart");
		await fixture.ReplyAsync(null, "echo: main after restart");
		workflow.Resume(run.Id);
		await Eventually(() => workflow.Current!.Phase == WorkflowPhase.Asking);

		File.Delete(Path.Combine(run.DocumentsDirectory, run.Prompt.Images[0].File));
		workflow.Answer(run.Id, "revise", "Use the image");
		await fixture.ReplyAsync(child, "Feedback:");
		Assert.Equal(WorkflowPhase.Open, workflow.Current!.Phase);
		Assert.Single(fixture.Messages, message => Type(message) == "user-image" && Conversation(message) == child);
		Assert.Single(fixture.Messages, message => Type(message) == "side-conversation-started" && Text(message) == "review");
	}

	[Fact]
	public async Task AChildSkillReceivesItsCommandImagesAndOwnedStageContext() {
		await using var fixture = await ConversationFixture.StartAsync(ImageFlow("fresh").Replace(
			"prompt: \"{{run.prompt}}\"", "skill: review, args: \"{{run.prompt}}\"", StringComparison.Ordinal));
		var session = fixture.Session;
		session.Workflow.Start(session.Workflow.Prepare("conversation", Json("{}"), null, ImagePrompt));
		var started = await fixture.MessageAsync(message => Type(message) == "side-conversation-started" && Text(message) == "review");
		var reply = await fixture.ReplyAsync(Conversation(started), "review command: review the change [1 image(s)]");
		using var wire = JsonDocument.Parse(File.ReadLines(Path.Combine(fixture.State("conversation-acp"), "wire-prompts.jsonl"))
			.Single(line => line.Contains(Thread(reply), StringComparison.Ordinal)));
		var blocks = wire.RootElement.GetProperty("parameters").GetProperty("prompt").EnumerateArray().ToArray();
		Assert.Equal("/review review the change", blocks[0].GetProperty("text").GetString());
		Assert.Equal("image", blocks[1].GetProperty("type").GetString());
		Assert.Equal(session.Workflow.Execution!.Compose(false), blocks[2].GetProperty("text").GetString());
	}
}
