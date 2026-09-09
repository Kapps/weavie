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
		Assert.False(host.WithCompletedPlan(threadId, turnId, itemId, _ => Assert.Fail("Unexpected plan")));

		session.Emit(new AgentPaneMessage {
			Type = "item-completed",
			ProviderId = "structured",
			ThreadId = threadId,
			TurnId = turnId,
			ItemId = itemId,
			ItemType = "plan",
			Text = "# Final plan",
		});
		Assert.True(host.WithCompletedPlan(threadId, turnId, itemId, plan => Assert.Equal("# Final plan", plan.Markdown)));
		Assert.False(host.WithCompletedPlan("another-thread", turnId, itemId, _ => Assert.Fail("Unexpected plan")));
		Assert.False(host.WithCompletedPlan(threadId, "another-turn", itemId, _ => Assert.Fail("Unexpected plan")));
		Assert.False(host.WithCompletedPlan(threadId, turnId, "another-item", _ => Assert.Fail("Unexpected plan")));

		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		Assert.False(host.WithCompletedPlan(threadId, turnId, itemId, _ => Assert.Fail("Unexpected plan")));
	}

	[Fact]
	public async Task PlanProjection_ReconcilesOnlyCompleteSnapshots() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		var snapshots = new List<IReadOnlyList<AgentPlan>>();
		host.PlanDocumentsChanged += snapshots.Add;
		var plan = Completed("plan", "# Original") with { ItemType = "plan" };
		session.Emit(plan);
		snapshots.Clear();

		session.Replace([plan with { Text = "# Complete revised document" }]);
		Assert.Equal("# Complete revised document", Assert.Single(Assert.Single(snapshots)).Markdown);
		snapshots.Clear();
		session.Replace([Completed("answer", "No plan remains")]);
		Assert.Empty(Assert.Single(snapshots));
	}

	[Fact]
	public async Task UntypedRetraction_InvalidatesThePlanDocument() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var plan = Completed("plan", "# Original") with { ItemType = "plan" };
		fixture.Session.Emit(plan);
		var snapshots = new List<IReadOnlyList<AgentPlan>>();
		fixture.Host.PlanDocumentsChanged += snapshots.Add;
		fixture.Session.Emit(plan with { Type = "item-retracted", ItemType = null, Text = null });
		Assert.Empty(Assert.Single(snapshots));
		Assert.False(fixture.Host.WithCompletedPlan(plan.ThreadId!, plan.TurnId!, plan.ItemId!,
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
		string? published = null;
		host.PlanDocumentsChanged += plans => published = plans.SingleOrDefault().Markdown;
		using var ready = new Barrier(2);

		var open = Task.Run(() => {
			ready.SignalAndWait(TimeSpan.FromSeconds(10));
			host.WithCompletedPlan(plan.ThreadId!, plan.TurnId!, plan.ItemId!, value => published = value.Markdown);
		});
		var update = Task.Run(() => {
			ready.SignalAndWait(TimeSpan.FromSeconds(10));
			session.Emit(plan with { Type = removed ? "item-retracted" : "item-completed", Text = "# Revised" });
		});
		await Task.WhenAll(open, update).WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(removed ? null : "# Revised", published);
	}
}
