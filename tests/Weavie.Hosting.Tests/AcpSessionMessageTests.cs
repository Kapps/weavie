using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSessionMessageTests {
	[Fact]
	public async Task ASessionMessageShowsItsSenderAndTellsOnlyTheAgentHowToReply() {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(false);
		await fixture.StartAsync();
		var sender = new AgentMessageSender("feat/other", "Other", SameSession: false);

		fixture.Session.Submit(AgentTurnSubmission.Prompt("ping") with { Sender = sender });
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		var prompt = Assert.Single(AcpPromptAssertions.Read(fixture));
		var guidance = Assert.Single(AcpPromptAssertions.Blocks(prompt),
			block => block.GetProperty("text").GetString() == EmbeddedAgentGuidance.SessionMessage(sender));
		Assert.Equal("assistant", Assert.Single(guidance.GetProperty("annotations").GetProperty("audience").EnumerateArray()).GetString());
		Assert.Contains(AcpPromptAssertions.Blocks(prompt), block => block.GetProperty("text").GetString() == "ping");
		var shown = Assert.Single(fixture.Messages, message => message.Type == "user-message");
		Assert.Equal(("ping", sender), (shown.Text, shown.Sender));
	}

	[Fact]
	public void AMessageFromAnotherConversationOfTheSessionHasNoReplyAddress() {
		string own = EmbeddedAgentGuidance.SessionMessage(new("feat/docs", "BTW", SameSession: true));
		string other = EmbeddedAgentGuidance.SessionMessage(new("feat/docs", "Docs", SameSession: false));

		Assert.Contains("""a message from "BTW", another conversation in your own Weavie session""", own, StringComparison.Ordinal);
		Assert.DoesNotContain("Reply with", own, StringComparison.Ordinal);
		Assert.Contains("""Reply with mcp__weavie__messageSession to id "feat/docs" only if""", other, StringComparison.Ordinal);
	}
}
