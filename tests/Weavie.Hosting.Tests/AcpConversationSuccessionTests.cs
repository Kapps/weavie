using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpConversationSuccessionTests {
	[Fact]
	public async Task SubmitAfterALaunchFailureStartsASuccessorThatDeliversThePrompt() {
		await using var fixture = AcpAgentSessionFixture.CreateLaunchFailingUntilRepaired(out var repair);
		fixture.Start();
		var failure = await fixture.WaitForMessageAsync(message => message.Type == "error");
		Assert.Contains("could not start", failure.Text, StringComparison.OrdinalIgnoreCase);

		repair();
		fixture.Submit("first question");
		var reply = await fixture.WaitForMessageAsync(message => message.Text == "echo: first question");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("1", reply.TurnId);
		Assert.Single(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task RestartSettlesThePredecessorsPendingApprovalAndRemembersIt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: false, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");

		fixture.Session.Restart();
		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved");
		await fixture.WaitForControlsAsync(state => state.Ready);
		fixture.Session.ResolvePermission(approval.RequestId!, "allow-once");

		Assert.Equal(approval.RequestId, resolved.RequestId);
		Assert.Equal("cancelled", resolved.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}
}
