using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPromptCompletionTests {
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CompletionQueuesReentrantInputUntilTheOldTurnIsPublished(bool cancel) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		bool queuedDuringCompletion = false;
		fixture.Events.Observed += QueueNextTurn;
		void QueueNextTurn(AgentEvent value) {
			if (value is not AgentTurnStopped) return;
			fixture.Events.Observed -= QueueNextTurn;
			fixture.SubmitCommand("hold-command", "/hold-command");
			queuedDuringCompletion = fixture.Session.QueuedSubmissions is [{ Text: "/hold-command" }];
		}

		fixture.Submit(cancel ? "hold-cancelled-request" : "alpha");
		if (cancel) {
			await fixture.WaitForHeldToolAsync();
			fixture.Session.Interrupt();
		}
		await fixture.WaitForHeldToolAsync(message => message.TurnId == "2");

		Assert.True(queuedDuringCompletion);
		var messages = fixture.Messages.ToArray();
		int completed = Array.FindIndex(messages, message => message.Type == "turn-completed" && message.TurnId == "1");
		int started = Array.FindIndex(messages, message => message.Type == "turn-started" && message.TurnId == "2");
		Assert.True(completed >= 0 && started > completed);
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RewindBeforeAsync("1"));
		Assert.Equal("Wait for the agent to finish before rewinding.", error.Message);

		File.WriteAllText(Path.Combine(fixture.Workspace, "release-hold"), string.Empty);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is null && message.TurnId == "2");
		await fixture.Session.RewindBeforeAsync("1");
		Assert.DoesNotContain(await fixture.WaitForSnapshotAsync(), message => message.Type == "turn-started");
	}
}
