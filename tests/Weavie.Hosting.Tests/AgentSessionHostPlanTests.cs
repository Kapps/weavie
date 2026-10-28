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
		host.PlansChanged += plans => snapshots.Add(plans.Documents);
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
		fixture.Host.PlansChanged += plans => snapshots.Add(plans.Documents);
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
		host.PlansChanged += plans => published = plans.Documents.SingleOrDefault().Markdown;
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

	[Fact]
	public async Task PlanReviews_ArePendingPlanApprovalsAndATextReviewIsADocument() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		var (session, host) = (fixture.Session, fixture.Host);
		var snapshots = new List<AgentPlansSnapshot>();
		host.PlansChanged += snapshots.Add;
		var file = Review("file", planPath: "/plans/claude.md", text: null);
		var text = Review("text", planPath: null, text: "# Codex plan");

		session.Emit(file);
		session.Emit(text);
		var reviews = snapshots[^1].Reviews;
		Assert.Equal(["file", "text"], reviews.Select(review => review.RequestId));
		Assert.Equal(("/plans/claude.md", true), (reviews[0].Path, reviews[0].Editable));
		var document = Assert.Single(snapshots[^1].Documents);
		Assert.Equal((AgentPlanProtocol.Path(document), false, "# Codex plan"), (reviews[1].Path, reviews[1].Editable, document.Markdown));
		Assert.True(host.WithCompletedPlan(text.ThreadId!, text.TurnId!, text.ItemId!, _ => { }));

		session.Emit(file with { Type = "approval-resolved", ItemType = null, PlanPath = null, Status = "allowed once" });
		Assert.Equal("text", Assert.Single(snapshots[^1].Reviews).RequestId);
		session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		Assert.Empty(snapshots[^1].Reviews);
	}

	private static AgentPaneMessage Review(string requestId, string? planPath, string? text) => new() {
		Type = "approval-requested",
		ProviderId = "structured",
		ThreadId = "thread",
		TurnId = "turn",
		ItemId = "request:" + requestId,
		RequestId = requestId,
		ItemType = "plan-review",
		Text = text,
		PlanPath = planPath,
		Actions = [new AgentActionOption { Id = "allow", Label = "Yes", Kind = "allow_once" }],
		Status = "pending",
	};
}
