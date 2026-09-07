using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpSideConcurrencyTests {
	[Theory]
	[InlineData("input")]
	[InlineData("permission")]
	public async Task ConcurrentRequestsAndRepliesKeepTheirConversationOwner(string prompt) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		string requestType = prompt == "input" ? "input-requested" : "approval-requested";
		fixture.Submit(prompt);
		var primary = await fixture.WaitForMessageAsync(message =>
			message.Type == requestType && message.ConversationId is null);
		fixture.Session.AskAside(prompt);
		var first = await fixture.WaitForMessageAsync(message =>
			message.Type == requestType && message.ConversationId is not null);
		fixture.Session.AskAside(prompt);
		var second = await fixture.WaitForMessageAsync(message => message.Type == requestType
			&& message.ConversationId is not null && message.ConversationId != first.ConversationId);
		Assert.Equal(3, new[] { primary.RequestId, first.RequestId, second.RequestId }.Distinct().Count());
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "turn-completed");

		Resolve(second, "two", "reject");
		await AssertAnswerAsync(second, prompt == "input" ? "input: two" : "permission: reject");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "turn-completed"
			&& message.ConversationId != second.ConversationId);

		fixture.Session.ReplyAside(Assert.IsType<string>(second.ConversationId), "identify-session");
		var secondIdentity = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.ConversationId == second.ConversationId && message.Text?.StartsWith("session:", StringComparison.Ordinal) == true);
		Resolve(first, "one", "allow-once");
		await AssertAnswerAsync(first, prompt == "input" ? "input: one" : "permission: allow-once");
		fixture.Session.ReplyAside(Assert.IsType<string>(first.ConversationId), "identify-session");
		var firstIdentity = await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
			&& message.ConversationId == first.ConversationId && message.Text?.StartsWith("session:", StringComparison.Ordinal) == true);
		Assert.NotEqual(firstIdentity.Text, secondIdentity.Text);
		Resolve(primary, "one", "allow-once");
		await AssertAnswerAsync(primary, prompt == "input" ? "input: one" : "permission: allow-once");

		string[] forks = File.ReadAllLines(Path.Combine(fixture.FakeAcpStateDirectory, "forks.log"));
		Assert.Equal(2, forks.Length);
		Assert.All(forks, fork => Assert.StartsWith("fake-session->", fork, StringComparison.Ordinal));
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "side-conversation-failed");

		void Resolve(AgentPaneMessage request, string choice, string permission) {
			if (prompt == "permission") fixture.Session.ResolvePermission(request.RequestId!, permission);
			else fixture.Session.ResolveInput(request.RequestId!, "accept",
				new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["choice"] = [choice] });
		}

		async Task AssertAnswerAsync(AgentPaneMessage request, string text) {
			await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
				&& message.ConversationId == request.ConversationId && message.Text == text);
			await fixture.WaitForMessageAsync(message => message.Type == "turn-completed"
				&& message.ConversationId == request.ConversationId && message.TurnId == "1");
		}
	}
}
