using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideForkTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptDuringForkIsolatesEarlyUpdatesAndAllowsTheNextAside(bool authenticationRequired) {
		await using var fixture = AcpAgentSessionFixture.CreateHeldForkAdapter(authenticationRequired);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.AskAside("interrupted side prompt");
		var first = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "fork-started")));
		fixture.Session.Interrupt();
		fixture.AskAside("next side prompt");
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
		string prompt = Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")).Skip(1));
		Assert.EndsWith(":next side prompt", prompt, StringComparison.Ordinal);
	}

	// Fixed 2026-09-09: AskAside called between Session.Start() (which only fires off the real subprocess
	// handshake) and that handshake's completion used to throw and silently drop the request — Submit() sent
	// in the same window is safely queued instead. Session.Start() is synchronous and returns long before the
	// spawned weavie-fake-acp process completes its initialize/session-new round trip, so calling AskAside
	// immediately after it (with no wait in between) reliably races the same window a real "very first
	// composer action" can.
	[Fact]
	public async Task AskAsideCalledBeforeReadyIsQueuedInsteadOfDropped() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		fixture.Session.Start();
		fixture.AskAside("queued before ready");
		var started = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		Assert.NotNull(started.ConversationId);
		await fixture.WaitForMessageAsync(message =>
			message.ConversationId == started.ConversationId && message.Text == "echo: queued before ready");
	}
}
