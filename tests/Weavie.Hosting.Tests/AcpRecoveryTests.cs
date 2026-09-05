using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpRecoveryTests {
	[Fact]
	public async Task MalformedSideUpdateStopsSharedWorkAndExplicitContinuationPreservesThePrimary() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		string? sessionId = fixture.Sessions.Resolve("fake", fixture.Workspace);
		fixture.Session.AskAside("malformed-update");
		await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-failed");

		fixture.Submit("primary still works");
		var reply = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.Text == "echo: primary still works");

		Assert.Null(reply.ConversationId);
		Assert.Contains(fixture.Events.Values, value => value is Weavie.Core.Agents.AgentRuntimeFailed);
		Assert.Equal(sessionId, fixture.Sessions.Resolve("fake", fixture.Workspace));
		Assert.Single(File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")),
			value => value.EndsWith(":malformed-update", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PrimaryDisposalDoesNotWaitForAChildCloseResponse() {
		await using var fixture = AcpAgentSessionFixture.CreateHeldCloseAdapter();
		await fixture.StartAsync();
		fixture.Session.AskAside("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is not null);

		await fixture.Session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task ExplicitContinuationRestoresTheSameConversationWithoutRepeatingTheFailedPrompt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("malformed-update");
		await fixture.WaitForMessageAsync(message => message.Type == "error");
		string? sessionId = fixture.Sessions.Resolve("fake", fixture.Workspace);

		fixture.Submit("continue");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.Status != "failed");

		Assert.Equal(sessionId, fixture.Sessions.Resolve("fake", fixture.Workspace));
		string[] prompts = File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log"));
		Assert.Equal(new[] { $"{sessionId}:malformed-update", $"{sessionId}:continue" }, prompts);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "transcript-reset");
	}
}
