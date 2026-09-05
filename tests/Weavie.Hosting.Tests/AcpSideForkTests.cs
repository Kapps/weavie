using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideForkTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptDuringForkIsolatesEarlyUpdatesAndAllowsTheNextAside(bool authenticationRequired) {
		await using var fixture = AcpAgentSessionFixture.CreateHeldForkAdapter(authenticationRequired);
		await fixture.StartAsync();
		fixture.Session.AskAside("interrupted side prompt");
		var first = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "fork-started")));
		fixture.Session.Interrupt();
		fixture.Session.AskAside("next side prompt");
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-fork"), string.Empty);

		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: next side prompt");
		Assert.Contains(fixture.Messages, message => message.Type == "side-conversation-failed"
			&& message.ConversationId == first.ConversationId);
		Assert.DoesNotContain(fixture.Messages, message => message.ConversationId != first.ConversationId
			&& message.Text == "early fork update");
		Assert.NotEqual(first.ConversationId, answer.ConversationId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "authentication-requested");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "side-conversation-failed"
			&& message.ConversationId == answer.ConversationId);
		string prompt = Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")));
		Assert.EndsWith(":next side prompt", prompt, StringComparison.Ordinal);
	}
}
