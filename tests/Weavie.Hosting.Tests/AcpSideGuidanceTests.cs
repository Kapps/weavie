using System.Text.Json;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideGuidanceTests {
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task ForkedAsideScopesEveryPromptWithoutTransferringPrimaryWork(bool embeddedContext) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(embeddedContext);
		var controls = await fixture.StartAsync();
		Assert.Contains(controls.Slash, command => command.Name == "btw");
		fixture.Submit("finish the primary task and merge it");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		fixture.AskAside("why did the check fail?");
		var first = await fixture.WaitForMessageAsync(message =>
			message.Type == "turn-completed" && message.ConversationId is not null);
		string conversation = Assert.IsType<string>(first.ConversationId);
		fixture.Session.ReplyAside(conversation, "explain the alternative");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.ConversationId == conversation && message.TurnId == "2");
		fixture.Submit("continue the primary task");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.ConversationId is null && message.TurnId == "2");

		var requests = AcpPromptAssertions.Read(fixture);
		Assert.Equal(4, requests.Length);
		AssertPrimaryScope(requests[0]);
		Assert.Equal(embeddedContext, HasGenericGuidance(requests[0]));
		foreach (var request in requests.Skip(1)) Assert.False(HasGenericGuidance(request));
		AcpPromptAssertions.SideScope(requests[1], "why did the check fail?");
		AcpPromptAssertions.SideScope(requests[2], "explain the alternative");
		AssertPrimaryScope(requests[3]);
		string sideSession = Assert.IsType<string>(requests[1].GetProperty("parameters")
			.GetProperty("sessionId").GetString());
		Assert.Equal(sideSession, requests[2].GetProperty("parameters").GetProperty("sessionId").GetString());
		Assert.Equal(sideSession, Assert.Single(File.ReadAllLines(
			Path.Combine(fixture.FakeAcpStateDirectory, "loads.log"))));
		Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
		Assert.Equal(["why did the check fail?", "explain the alternative"],
			fixture.Messages.Where(message => message.Type == "user-message"
				&& message.ConversationId == conversation).Select(message => message.Text));
		Assert.DoesNotContain(fixture.Messages, message =>
			message.Text?.Contains(EmbeddedAgentGuidance.SideConversationInstructions, StringComparison.Ordinal) == true);
	}

	[Fact]
	public async Task SideSteeringKeepsItsRoleGuidanceSeparateFromPrimarySteering() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.Type == "item-started"
			&& message.ItemId == "tool:hold" && message.ConversationId is null);
		fixture.AskAside("hold");
		var held = await fixture.WaitForMessageAsync(message => message.Type == "item-started"
			&& message.ItemId == "tool:hold" && message.ConversationId is not null);
		string conversation = Assert.IsType<string>(held.ConversationId);
		fixture.Session.ReplyAside(conversation, "answer only the side question");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.ConversationId == conversation);
		fixture.Submit("finish the primary independently");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.ConversationId is null);

		var requests = AcpPromptAssertions.Read(fixture);
		Assert.Equal(4, requests.Length);
		Assert.Equal(["session/prompt", "session/prompt", "_session/steering", "_session/steering"],
			requests.Select(request => request.GetProperty("method").GetString()));
		AssertPrimaryScope(requests[0]);
		AcpPromptAssertions.SideScope(requests[1], "hold");
		AcpPromptAssertions.SideScope(requests[2], "answer only the side question");
		AssertPrimaryScope(requests[3]);
	}

	private static bool HasGenericGuidance(JsonElement request) => AcpPromptAssertions.Blocks(request)
		.Any(block => block.GetProperty("type").GetString() == "resource"
			&& block.GetProperty("resource").GetProperty("uri").GetString() == "weavie://instructions");

	private static void AssertPrimaryScope(JsonElement request) => Assert.DoesNotContain(
		AcpPromptAssertions.Blocks(request), block => block.GetProperty("type").GetString() == "text"
			&& block.GetProperty("text").GetString() == EmbeddedAgentGuidance.SideConversationInstructions);
}
