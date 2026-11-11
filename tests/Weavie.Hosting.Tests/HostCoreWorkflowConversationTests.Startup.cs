using Weavie.Workflows;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class HostCoreWorkflowConversationTests {

	[Theory]
	[InlineData("fresh")]
	[InlineData("fork")]
	public async Task NewSessionsWorkflowOpensItsChildAfterTheMainAgentIsReady(string context) {
		await using var fixture = await ConversationFixture.NewWorkflowAsync(context);
		string child = await fixture.WaitForReviewAsync();
		Assert.Equal(WorkflowPhase.Open, fixture.Session.Workflow.Current!.Phase);
		Assert.Null(fixture.Session.Workflow.Current.PausedReason);
		Assert.DoesNotContain(fixture.Messages, message => Conversation(message) is null && Type(message) == "user-message");
		fixture.Send(child, Completion);
		await fixture.ReplyAsync(child, "completeStage: Complete.");
		await fixture.ReplyAsync(null, "now asks the user: Accept reviewed change?");
		Assert.Equal(WorkflowPhase.Asking, fixture.Session.Workflow.Current!.Phase);
	}

}
