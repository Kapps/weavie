using System.Text.Json;
using Weavie.Core.Agents;
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
	public async Task RestoredAsidePreservesItsImageAndRoleWithoutDisplayingFlattenedProviderGuidance() {
		await using var fixture = AcpAgentSessionFixture.CreateFlattenReplayAdapter("saved-primary");
		var primary = Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace));
		var side = primary with {
			ConversationId = "saved-aside",
			SessionId = "saved-child",
			AnchorTurnNumber = 1,
			InitialPrompt = "explain this image",
			TurnNumber = 1,
		};
		const string imageData = "iVBORwECAw==";
		var image = new AgentPaneMessage {
			Type = "user-image",
			ProviderId = "fake",
			ConversationId = side.ConversationId,
			ThreadId = side.SessionId,
			TurnId = "1",
			ItemId = "saved-image",
			MediaType = "image/png",
			MediaData = imageData,
			Status = "submitted",
		};
		fixture.Sessions.Save("fake", fixture.Workspace, side, [
			new AgentPaneMessage {
				Type = "user-message", ProviderId = "fake", ConversationId = side.ConversationId,
				ThreadId = side.SessionId, TurnId = "1", ItemId = "saved-question", Text = side.InitialPrompt,
			},
			image,
		]);
		Directory.CreateDirectory(fixture.FakeAcpStateDirectory);
		await File.WriteAllTextAsync(Path.Combine(fixture.FakeAcpStateDirectory, "session-transcript-saved-child.log"),
			JsonSerializer.Serialize(new object[] {
				new { type = "text", text = side.InitialPrompt },
				new { type = "image", mimeType = image.MediaType, data = imageData },
				new { type = "text", text = EmbeddedAgentGuidance.SideConversationInstructions,
					annotations = new { audience = new[] { "assistant" } } },
			}) + Environment.NewLine);

		await fixture.StartAsync();
		var snapshot = await fixture.WaitForSnapshotAsync();
		Assert.Equal(image, Assert.Single(snapshot, message => message.Type == "user-image"));
		Assert.DoesNotContain(snapshot, message => message.Text == EmbeddedAgentGuidance.SideConversationInstructions);
		fixture.Session.ReplyAside(side.ConversationId, "explain one more detail");
		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.ConversationId == side.ConversationId);

		Assert.Equal(side.SessionId, completed.ThreadId);
		Assert.Equal("2", completed.TurnId);
		var request = Assert.Single(AcpPromptAssertions.Read(fixture));
		Assert.Equal(side.SessionId, request.GetProperty("parameters").GetProperty("sessionId").GetString());
		AcpPromptAssertions.SideScope(request, "explain one more detail");
		Assert.False(HasGenericGuidance(request));
		Assert.Equal(["saved-primary", "saved-child"], File.ReadAllLines(
			Path.Combine(fixture.FakeAcpStateDirectory, "loads.log")));
		Assert.False(File.Exists(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log")));
		var display = fixture.Sessions.ReadMessages("fake", fixture.Workspace);
		Assert.Equal(image, Assert.Single(display, message => message.Type == "user-image"));
		Assert.DoesNotContain(display, message =>
			message.Text?.Contains(EmbeddedAgentGuidance.SideConversationInstructions, StringComparison.Ordinal) == true);
		Assert.Equal([side.InitialPrompt, "explain one more detail"],
			display.Where(message => message.Type == "user-message").Select(message => message.Text));
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
