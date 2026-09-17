using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideCommandTests {
	[Theory]
	[InlineData(AgentTurnSubmissionKind.ProviderCommand, "review", "focus on tests")]
	[InlineData(AgentTurnSubmissionKind.McpPrompt, "report-weavie-bug", "invented details")]
	public async Task ForkUsesTheSameInvocationPathAsThePrimary(AgentTurnSubmissionKind kind, string name, string details) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("main question");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		string? primaryId = fixture.Sessions.Resolve("fake", fixture.Workspace);
		string text = $"/{name} {details}";

		fixture.Session.AskAside(Submission(kind, name, text));
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is not null);

		Assert.NotEqual(primaryId, completed.ThreadId);
		Assert.Equal(primaryId, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Equal(1, fixture.Sessions.ResolveTurnNumber("fake", fixture.Workspace));
		var request = AcpPromptAssertions.Read(fixture).Last();
		Assert.Equal(completed.ThreadId, request.GetProperty("parameters").GetProperty("sessionId").GetString());
		var blocks = AcpPromptAssertions.Blocks(request).ToArray();
		if (kind == AgentTurnSubmissionKind.ProviderCommand) {
			Assert.Equal(text, Assert.Single(blocks).GetProperty("text").GetString());
		} else {
			Assert.Equal(McpPromptCatalog.Require(name).Text, blocks[0].GetProperty("text").GetString());
			Assert.Equal(details, blocks[1].GetProperty("text").GetString());
		}
		string submittedType = kind == AgentTurnSubmissionKind.ProviderCommand ? "user-command" : "user-message";
		Assert.Contains(fixture.Messages, message => message.Type == submittedType && message.ConversationId == completed.ConversationId && message.Text == text);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task ForkValidatesCommandsAgainstItsOwnCatalog() {
		await using var fixture = AcpAgentSessionFixture.CreateWithoutForkCommands();
		await fixture.StartAsync();
		fixture.Submit("main question");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		fixture.Session.AskAside(Submission(AgentTurnSubmissionKind.ProviderCommand, "review", "/review tests"));
		var error = await fixture.WaitForMessageAsync(message => message.Type == "error" && message.ConversationId is not null);

		Assert.Contains("no longer advertises", error.Text);
		Assert.Single(AcpPromptAssertions.Read(fixture));
		fixture.Submit("main still works");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: main still works" && message.ConversationId is null);
	}

	private static AgentTurnSubmission Submission(AgentTurnSubmissionKind kind, string name, string text) => new() {
		Id = Guid.NewGuid().ToString("N"),
		Text = text,
		Kind = kind,
		CommandName = name,
		Attachments = [],
	};
}
