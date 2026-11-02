using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPlanModeTests {
	[Fact]
	public async Task ExitPlanMode_ReviewsThePlanFileAndApprovesItsEditedContent() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Session.SetControl("mode", "plan");
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "mode" && axis.Value == "plan"));

		fixture.Submit("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		string planFile = Path.Combine(fixture.FakeAcpStateDirectory, "plans", "fake-plan.md");
		Assert.Equal(("plan-review", planFile, null), (approval.ItemType, approval.PlanPath, approval.Text));
		Assert.Equal("plan:plan-mode:toolu_plan_1", approval.ItemId);
		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "plan");
		File.AppendAllText(planFile, "Edited in the editor.\n");

		fixture.Session.ResolvePermission(approval.RequestId!, "exit-plan-default");
		var echo = await fixture.WaitForMessageAsync(message => message.Text?.StartsWith("approved plan:", StringComparison.Ordinal) == true);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.EndsWith("Edited in the editor.\n", echo.Text, StringComparison.Ordinal);
		var resolved = Assert.Single(fixture.Messages, message => message.Type == "approval-resolved");
		Assert.Equal((approval.ItemId, "Approved · Yes, manually approve edits"), (resolved.ItemId, resolved.Status));
		var plans = fixture.Messages.Where(message => message.ItemType == "plan").ToArray();
		Assert.NotEmpty(plans);
		Assert.All(plans, plan => {
			Assert.Equal(("plan:plan-mode:toolu_plan_1", approval.TurnId), (plan.ItemId, plan.TurnId));
			Assert.EndsWith("Edited in the editor.\n", plan.Text, StringComparison.Ordinal);
		});
		Assert.DoesNotContain(fixture.Messages, message => message.ItemId?.EndsWith(":plan-file-clear", StringComparison.Ordinal) == true);
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "mode" && axis.Value == "default"));
		Assert.Equal("default", fixture.ControlDefaults.Resolve("fake")["mode"]);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClearContext_IsRefusedOnlyForAnEditedPlan(bool edited) {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		if (edited) File.AppendAllText(approval.PlanPath!, "Edited in the editor.\n");

		fixture.Session.ResolvePermission(approval.RequestId!, "exit-plan-clear-bypass");
		if (edited) {
			var failure = await fixture.WaitForMessageAsync(message => message.Type == "error");
			Assert.StartsWith("Clear context would discard your plan edits.", failure.Text, StringComparison.Ordinal);
			Assert.DoesNotContain(fixture.Messages, message => message.Type == "approval-resolved");
			fixture.Session.ResolvePermission(approval.RequestId!, "exit-plan-default");
		}
		await fixture.WaitForMessageAsync(message => message.Text?.StartsWith("approved plan:", StringComparison.Ordinal) == true);
	}

	[Fact]
	public async Task ExitPlanMode_KeepPlanningPublishesNoPlan() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");

		fixture.Session.ResolvePermission(approval.RequestId!, "reject");
		await fixture.WaitForMessageAsync(message => message.Text == "kept planning");

		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "plan");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task ModeSwitchWithPlanText_StaysAnApprovalInThePane() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("permission-lifecycle:plan");

		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.Equal(("switch_mode", null), (approval.ItemType, approval.PlanPath));
	}

	[Fact]
	public async Task InterruptedReview_IgnoresALateAcceptance() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");

		fixture.Session.Interrupt();
		await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved" && message.Status == "cancelled");
		fixture.Session.ResolvePermission(approval.RequestId!, "exit-plan-default");

		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "plan");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task Aside_ReviewsItsPlanInItsOwnConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		fixture.AskAside("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.NotNull(approval.ConversationId);
		Assert.Equal(approval.ConversationId + ":", approval.RequestId![..(approval.ConversationId.Length + 1)]);

		fixture.Session.ResolvePermission(approval.RequestId, "exit-plan-default");
		var plan = await fixture.WaitForMessageAsync(message => message.ItemType == "plan");
		Assert.Equal(approval.ConversationId, plan.ConversationId);
		await fixture.WaitForMessageAsync(message => message.Text?.StartsWith("approved plan:", StringComparison.Ordinal) == true);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	// Rewind waits for a side's pending review, so only a restart can interrupt it.
	[Fact]
	public async Task AsideReview_IsSettledWhenTheAgentRestarts() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		foreach (string prompt in new[] { "alpha", "bravo" }) {
			fixture.Submit(prompt);
			await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: " + prompt);
		}
		fixture.AskAside("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");

		await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Session.RewindBeforeAsync("2"));
		fixture.Session.Restart();

		var resolved = await fixture.WaitForMessageAsync(message => message.Type == "approval-resolved");
		Assert.Equal((approval.RequestId, "cancelled"), (resolved.RequestId, resolved.Status));
	}

	[Fact]
	public async Task Aside_StartsInThePrimarysLiveModeRatherThanTheProviderDefault() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("primary context");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		// Another session of this provider chose plan mode after this one opened.
		fixture.ControlDefaults.Set("fake", "mode", "plan");

		fixture.AskAside("control-state");

		var state = await fixture.WaitForMessageAsync(message =>
			message.ConversationId is not null && message.Text?.StartsWith("control state:", StringComparison.Ordinal) == true);
		Assert.Equal("control state: alpha/default/False", state.Text);
		Assert.Equal("plan", fixture.ControlDefaults.Resolve("fake")["mode"]);
	}
}
