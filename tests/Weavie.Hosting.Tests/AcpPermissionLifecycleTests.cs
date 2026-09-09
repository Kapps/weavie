using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPermissionLifecycleTests {
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Disposal_SettlesPermissionOnlyMutationObservations(bool allowAll) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: allowAll, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:edit-hold");
		await fixture.WaitForMessageAsync(message => allowAll
			? message.Text == "permission approved and held"
			: message.Type == "approval-requested");
		Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>());
		Assert.Empty(fixture.Events.Values.OfType<AgentToolCompleted>());

		await fixture.Session.DisposeAsync();
		Assert.Equal(
			Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>()).Mutation,
			Assert.Single(fixture.Events.Values.OfType<AgentToolCompleted>()).Mutation);
	}

	[Fact]
	public async Task Disposal_SettlesSidePermissionMutationsBeforeRetiringTheirOwner() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Session.AskAside("permission-lifecycle:edit-hold");
		var held = await fixture.WaitForMessageAsync(message => message.Text == "permission approved and held");
		await fixture.Session.DisposeAsync();

		var events = fixture.Events.Values.OfType<AgentConversationEvent>()
			.Where(value => value.ConversationId == held.ConversationId).Select(value => value.Value).ToArray();
		Assert.Equal(Assert.Single(events.OfType<AgentToolStarting>()).Mutation,
			Assert.Single(events.OfType<AgentToolCompleted>()).Mutation);
	}

	[Fact]
	public async Task Restart_SettlesPermissionObservationsAndReadmitsIdsInTheNewGeneration() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:edit-hold");
		await fixture.WaitForMessageAsync(message => message.Text == "permission approved and held");
		fixture.Session.Restart();
		fixture.Submit("permission-lifecycle:initial");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");

		Assert.Contains(fixture.Messages, message => message.ItemType == "tool" && message.TurnId == "2"
			&& message.Type == "item-completed" && message.Status == "completed");
		Assert.Equal(fixture.Events.Values.OfType<AgentToolStarting>().Count(),
			fixture.Events.Values.OfType<AgentToolCompleted>().Count());
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData("first")]
	[InlineData("immediate")]
	[InlineData("existing")]
	[InlineData("initial")]
	[InlineData("id-only")]
	public async Task PermissionTool_ReceivesLaterNotificationsWithoutFailing(string scenario) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:" + scenario);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		var tool = Assert.Single(fixture.Messages, message => message.ItemType == "tool" && message.Type == "item-completed");
		Assert.Equal("completed", tool.Status);
		Assert.Equal("1", tool.TurnId);
		if (scenario != "id-only") Assert.Equal("Protected operation", tool.Summary);
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "error" or "approval-requested");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData(true, "plan", "allow")]
	[InlineData(true, "plan", "reject")]
	[InlineData(true, "plan-existing", "allow")]
	[InlineData(false, "plan", "allow")]
	[InlineData(false, "plan", "reject")]
	public async Task PlanReview_AlwaysWaitsForTheUsersChoice(bool allowAll, string scenario, string choice) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: allowAll, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:" + scenario);
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");

		Assert.Equal("switch_mode", approval.Category);
		Assert.Equal("Implement this plan?", approval.Summary);
		Assert.Contains("End of the complete work plan.", approval.Text, StringComparison.Ordinal);
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "turn-completed");
		fixture.Session.ResolvePermission(approval.RequestId!, choice);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Contains(fixture.Messages, message => message.Text == "permission lifecycle: " + choice);
		Assert.Contains(fixture.Messages, message => message.ItemType == "tool" && message.Type == "item-completed"
			&& message.Status == (choice == "allow" ? "completed" : "failed"));
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task PlanReview_CancellationSettlesTheRequestAndAllowsAnotherPrompt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:plan");
		await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		fixture.Session.Interrupt();
		await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved" && message.Status == "cancelled");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Submit("after plan cancellation");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == "2");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData("unknown", "updated unknown tool call")]
	[InlineData("duplicate", "started more than once")]
	public async Task InvalidToolLifecycle_RemainsAVisibleFailure(string scenario, string error) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:" + scenario);
		var failure = await fixture.WaitForMessageAsync(message => message.Type == "error");
		Assert.Contains(error, failure.Text, StringComparison.Ordinal);
		Assert.Equal(SessionStatus.Error, fixture.Events.Status.Status);
	}

	[Theory]
	[InlineData("edit")]
	[InlineData("edit-only")]
	public async Task PermissionMetadata_CapturesTheBaselineBeforeApprovalAndCompletesOnce(string scenario) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		string path = Path.Combine(fixture.Workspace, "permission-edit.txt");
		await File.WriteAllTextAsync(path, "before permission\n");
		var baseline = fixture.Events.BlockNext<AgentToolStarting>();
		fixture.Submit("permission-lifecycle:" + scenario);
		try {
			await baseline.Entered.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal("before permission\n", await File.ReadAllTextAsync(path));
		} finally {
			baseline.Release();
		}
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal("after permission\n", await File.ReadAllTextAsync(path));
		var start = Assert.Single(fixture.Events.Values.OfType<AgentToolStarting>());
		var completed = Assert.Single(fixture.Events.Values.OfType<AgentToolCompleted>());
		Assert.Equal(path, Assert.IsType<AgentMutation.File>(start.Mutation).Path);
		Assert.Equal(start.Mutation, completed.Mutation);
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
		if (scenario == "edit-only") Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "tool");
	}

	[Fact]
	public async Task SidePlanReview_ResolvesOnlyItsOwningConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Session.AskAside("permission-lifecycle:plan");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.NotNull(approval.ConversationId);
		fixture.Session.ResolvePermission(approval.RequestId!, "allow");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId == approval.ConversationId);
		var tool = Assert.Single(fixture.Messages, message => message.ItemType == "tool" && message.Type == "item-completed");
		Assert.Equal(approval.ConversationId, tool.ConversationId);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}
}
