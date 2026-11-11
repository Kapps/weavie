using Weavie.Core.Agents;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class AgentSessionHostTests {
	[Fact]
	public async Task CompletedPlan_IsAvailableOnlyForItsExactCurrentIdentity() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		const string threadId = "thread-plan";
		const string turnId = "turn-plan";
		const string itemId = "item-plan";

		session.Emit(new AgentPaneMessage {
			Type = "plan-delta",
			ProviderId = "structured",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Draft",
		});
		Assert.False(host.WithCompletedPlan("structured", null, threadId, turnId, itemId, _ => Assert.Fail("Unexpected plan")));

		session.Emit(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "structured",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Final plan",
		});
		Assert.True(host.WithCompletedPlan("structured", null, threadId, turnId, itemId, plan => Assert.Equal("# Final plan", plan.Markdown)));
		Assert.False(host.WithCompletedPlan("structured", null, "another-thread", turnId, itemId, _ => Assert.Fail("Unexpected plan")));
		Assert.False(host.WithCompletedPlan("structured", null, threadId, "another-turn", itemId, _ => Assert.Fail("Unexpected plan")));
		Assert.False(host.WithCompletedPlan("structured", null, threadId, turnId, "another-item", _ => Assert.Fail("Unexpected plan")));

		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.False(host.WithCompletedPlan("structured", null, threadId, turnId, itemId, _ => Assert.Fail("Unexpected plan")));
	}

	[Fact]
	public async Task PlanProjection_ReconcilesOnlyCompleteSnapshots() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		var snapshots = new List<IReadOnlyList<AgentPlan>>();
		host.PlansChanged += plans => snapshots.Add(plans.Documents);
		var plan = Completed("plan", "# Original") with { ItemType = "plan" };
		session.Emit(plan);
		await host.DrainPaneAsync(CancellationToken.None);
		snapshots.Clear();

		session.Replace([plan with { Text = "# Complete revised document" }]);
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Equal("# Complete revised document", Assert.Single(Assert.Single(snapshots)).Markdown);
		snapshots.Clear();
		session.Replace([Completed("answer", "No plan remains")]);
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Empty(Assert.Single(snapshots));
	}

	[Fact]
	public async Task UntypedRetraction_InvalidatesThePlanDocument() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var plan = Completed("plan", "# Original") with { ItemType = "plan" };
		fixture.Session.Emit(plan);
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var snapshots = new List<IReadOnlyList<AgentPlan>>();
		fixture.Host.PlansChanged += plans => snapshots.Add(plans.Documents);
		fixture.Session.Emit(plan with { Type = "item-retracted", ItemType = null, Text = null });
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		Assert.Empty(Assert.Single(snapshots));
		Assert.False(fixture.Host.WithCompletedPlan("structured", null, plan.ThreadId!, plan.TurnId!, plan.ItemId!,
			_ => Assert.Fail("Retracted plan must not open")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OpeningDuringAPlanChange_CannotPublishAnOlderDocumentLast(bool removed) {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		var plan = Completed("plan", "# Original") with { ItemType = "plan" };
		session.Emit(plan);
		await host.DrainPaneAsync(CancellationToken.None);
		string? published = null;
		host.PlansChanged += plans => published = plans.Documents.SingleOrDefault().Markdown;
		using var ready = new Barrier(2);

		var open = Task.Run(() => {
			ready.SignalAndWait(TimeSpan.FromSeconds(10));
			host.WithCompletedPlan("structured", null, plan.ThreadId!, plan.TurnId!, plan.ItemId!, value => published = value.Markdown);
		});
		var update = Task.Run(() => {
			ready.SignalAndWait(TimeSpan.FromSeconds(10));
			session.Emit(plan with { Type = removed ? "item-retracted" : "item-completed", Text = "# Revised" });
		});
		await Task.WhenAll(open, update).WaitAsync(TimeSpan.FromSeconds(10));
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Equal(removed ? null : "# Revised", published);
	}

	[Fact]
	public async Task PlanReviews_ArePendingPlanFileApprovals() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		var snapshots = new List<AgentPlansSnapshot>();
		host.PlansChanged += snapshots.Add;
		var first = Review("first", "/plans/first.md");
		var second = Review("second", "/plans/second.md");

		session.Emit(first);
		session.Emit(second);
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Equal([("first", "/plans/first.md"), ("second", "/plans/second.md")],
			snapshots[^1].Reviews.Select(review => (review.RequestId, review.Path)));
		Assert.Empty(snapshots[^1].Documents);

		session.Emit(first with { Type = "approval-resolved", ItemType = null, PlanPath = null, Status = "allowed once" });
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Equal("second", Assert.Single(snapshots[^1].Reviews).RequestId);
		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		await host.DrainPaneAsync(CancellationToken.None);
		Assert.Empty(snapshots[^1].Reviews);
	}

	private static AgentPaneMessage Review(string requestId, string planPath) => new() {
		Type = "approval-requested",
		ProviderId = "structured",
		ThreadId = "thread",
		TurnId = "turn",
		ItemId = "request:" + requestId,
		RequestId = requestId,
		ItemType = "plan-review",
		PlanPath = planPath,
		Actions = [new AgentActionOption { Id = "allow", Label = "Yes", Kind = "allow_once" }],
		Status = "pending",
	};
}
