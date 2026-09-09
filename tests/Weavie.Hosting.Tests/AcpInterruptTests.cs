using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpInterruptTests {
	[Theory]
	[InlineData(true, "hold")]
	[InlineData(true, "hold-cancelled-request")]
	[InlineData(false, "hold")]
	[InlineData(false, "hold-cancelled-request")]
	public async Task InterruptDrainsAcceptedSubmissionsInOrder(bool supportsSteering, string heldPrompt) {
		await using var fixture = supportsSteering
			? AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null)
			: AcpAgentSessionFixture.CreateWithoutSteeringAdapter();
		await fixture.StartAsync();
		fixture.Submit(heldPrompt);
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.SubmitCommand("compact", "/compact");
		fixture.SubmitCommand("review", "/review queued work");
		if (!supportsSteering) fixture.Submit("queued message");
		await fixture.WaitForQueueAsync(queued => queued.Count == (supportsSteering ? 2 : 3));

		fixture.Session.Interrupt();
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.TurnId == (supportsSteering ? "3" : "4"));
		await fixture.WaitForQueueAsync(queued => queued.Count == 0);
		fixture.Submit("after interrupt");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
			&& message.TurnId == (supportsSteering ? "4" : "5"));

		string[] expected = [heldPrompt, "/compact", "/review queued work",
			.. supportsSteering ? Array.Empty<string>() : ["queued message"], "after interrupt"];
		Assert.Equal(expected.Select(text => "fake-session:" + text),
			File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")));
		Assert.Equal("cancelled", Assert.Single(fixture.Messages,
			message => message.Type == "turn-completed" && message.TurnId == "1").Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.False(File.Exists(Path.Combine(fixture.Workspace, "command-steered")));
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task InterruptRetainsAnUnacknowledgedSteeringSubmission() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("hold");
		await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		fixture.SubmitCommand("compact", "/compact");
		fixture.Submit("held-steering");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "steering-started")));

		fixture.Session.Interrupt();
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.Status == "cancelled");
		Assert.Equal("/compact", Assert.Single(fixture.Session.QueuedSubmissions).Text);
		File.WriteAllText(Path.Combine(fixture.Workspace, "release-steering"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "3");

		Assert.Equal(["fake-session:hold", "fake-session:held-steering", "fake-session:/compact"],
			File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")));
		Assert.Empty(fixture.Session.QueuedSubmissions);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task InterruptPreservesQueuedRepliesToAnActiveSideConversation() {
		await using var fixture = AcpAgentSessionFixture.CreateWithoutSteeringAdapter();
		await fixture.StartAsync();
		fixture.Session.AskAside("hold");
		var held = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:hold" && message.Type == "item-started");
		string conversationId = Assert.IsType<string>(held.ConversationId);
		fixture.Session.ReplyAside(conversationId, "queued side reply");

		fixture.Session.Interrupt();
		var reply = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: queued side reply");

		Assert.Equal(conversationId, reply.ConversationId);
		Assert.Contains(fixture.Messages, message => message.Type == "turn-completed"
			&& message.ConversationId == conversationId && message.Status == "cancelled");
		Assert.Equal([$"{held.ThreadId}:hold", $"{held.ThreadId}:queued side reply"],
			File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")));
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "error" or "side-conversation-failed");
	}
}
