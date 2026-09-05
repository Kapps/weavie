using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideAuthenticationTests {
	[Fact]
	public async Task SideTerminalAuthenticationRestartsOwnerWithoutReplayingSidePrompt() {
		await using var fixture = AcpAgentSessionFixture.CreateSideTerminalAuthenticationAdapter();
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var starts = fixture.Events.Values.OfType<AgentSessionStarted>().ToHashSet(ReferenceEqualityComparer.Instance);
		fixture.Session.AskAside("side prompt must not replay");
		var authentication = await fixture.WaitForMessageAsync(message =>
			message.Type == "authentication-requested" && message.ConversationId is not null);

		fixture.Session.Authenticate(
			Assert.IsType<string>(authentication.RequestId),
			"fake-terminal-login",
			new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
		await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-failed"
			&& message.ConversationId == authentication.ConversationId);
		await fixture.Events.WaitForAsync(value => value is AgentSessionStarted started && !starts.Contains(started));
		await fixture.WaitForControlsAsync(state => state.Axes.Count == 0);
		await fixture.WaitForControlsAsync(state => state.Axes.Count > 0);

		fixture.Submit("after side authentication");
		var answer = await fixture.WaitForMessageAsync(message =>
			message.Type == "item-completed" && message.Text == "echo: after side authentication");
		Assert.Null(answer.ConversationId);
		Assert.NotNull(fixture.AuthenticationLaunch);
		Assert.Equal(
			["fake-session:primary context", "fake-session:after side authentication"],
			File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "prompts.log")));
		Assert.Single(fixture.Messages, message => message.Type == "authentication-requested");
	}
}
