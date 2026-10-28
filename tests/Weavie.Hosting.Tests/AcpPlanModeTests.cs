using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpPlanModeTests {
	[Fact]
	public async Task ExitPlanMode_ReviewsItsPlanAsOneDocumentAndRemembersTheApprovedMode() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Session.SetControl("mode", "plan");
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "mode" && axis.Value == "plan"));

		fixture.Submit("exit-plan-mode");
		var approval = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		var review = Assert.Single(fixture.Messages, message => message.ItemType == "plan");
		Assert.Equal("plan:plan-mode:toolu_plan_1", review.ItemId);
		Assert.Equal("# Claude plan\n\nCreate b.txt.", review.Text);
		Assert.All(fixture.Messages.Where(message => message.ItemType == "tool"), tool => Assert.Null(tool.Content));

		fixture.Session.ResolvePermission(approval.RequestId!, "exit-plan-default");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		await fixture.WaitForControlsAsync(state => state.Axes.Any(axis => axis.Id == "mode" && axis.Value == "default"));

		var plans = fixture.Messages.Where(message => message.ItemType == "plan").ToArray();
		Assert.All(plans, plan => Assert.Equal((review.ItemId, review.TurnId), (plan.ItemId, plan.TurnId)));
		Assert.EndsWith("Edited on approval.", plans[^1].Text, StringComparison.Ordinal);
		Assert.Equal("default", fixture.ControlDefaults.Resolve("fake")["mode"]);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
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
