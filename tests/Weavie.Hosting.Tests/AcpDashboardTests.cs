using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Blocks shown into a dashboard share one journaled dashboard row in their conversation.</summary>
public sealed class AcpDashboardTests {
	private static string Chart(string id) =>
		$$$"""{"id":"{{{id}}}","title":"{{{id}}} chart","dashboard":{"id":"rollout","title":"Checkout rollout"},"chart":{"kind":"line","x":{"type":"number"},"series":[{"name":"now","points":[[1,2]]}]}}""";

	[Fact]
	public async Task TheFirstBlockCreatesTheDashboard_AndALaterTurnsBlockJoinsItWithALine() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block:claude:showBlock:{Chart("p95")}", 1);
		await CallAsync(fixture, $"block:codex:showBlock:{Chart("errors")}", 2);

		var rows = Rows(fixture);
		var marker = Assert.Single(rows, row => row.ItemType == AgentDashboards.ItemType);
		Assert.Equal(("item-completed", "dashboard:rollout", "Checkout rollout", "1"), (marker.Type, marker.ItemId, marker.Summary, marker.TurnId));
		var first = Assert.Single(rows, row => row.BlockId == "p95");
		Assert.Equal(("dashboard:rollout", (string?)null), (first.ParentItemId, first.Text));
		var later = Assert.Single(rows, row => row.BlockId == "errors");
		Assert.Equal(("dashboard:rollout", "added to Checkout rollout"), (later.ParentItemId, later.Text));
		var saved = fixture.Sessions.ReadMessages("fake", fixture.Workspace);
		Assert.Single(saved, row => row.ItemType == AgentDashboards.ItemType);
		Assert.Equal("added to Checkout rollout", Assert.Single(saved, row => row.BlockId == "errors" && row.Type == "item-completed").Text);
	}

	[Fact]
	public async Task ParallelShowsIntoANewDashboard_CreateItOnce() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block-parallel:claude:showBlock:{Chart("a")}\n{Chart("b")}", 1);

		var rows = Rows(fixture);
		Assert.Single(rows, row => row.ItemType == AgentDashboards.ItemType);
		Assert.All(rows.Where(row => row.ItemType == "block"), row => Assert.Equal(("dashboard:rollout", (string?)null), (row.ParentItemId, row.Text)));
	}

	[Fact]
	public async Task ARefusedCreatingTurn_RetractsTheDashboardWithItsBlocks() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block-refused:first:showBlock:{Chart("p95")}", 1);

		Assert.Null(fixture.Session.Output.View("fake", null).Dashboard("rollout"));
		Assert.Null(fixture.Session.Output.Block("fake", null, "p95"));
	}

	[Fact]
	public async Task RewindBeforeTheAddingTurn_KeepsTheDashboardWithItsEarlierBlocks() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart("p95")}", 1);
		await CallAsync(fixture, "alpha", 2);
		await CallAsync(fixture, $"block:claude:showBlock:{Chart("errors")}", 3);
		await fixture.WaitForControlsAsync(state => state.Rewindable);

		await fixture.Session.RewindBeforeAsync("3");
		await fixture.WaitForSnapshotAsync();

		Assert.NotNull(fixture.Session.Output.View("fake", null).Dashboard("rollout"));
		Assert.NotNull(fixture.Session.Output.Block("fake", null, "p95"));
		Assert.Null(fixture.Session.Output.Block("fake", null, "errors"));
	}

	[Fact]
	public async Task ASideConversationsDashboard_IsItsOwn() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart("p95")}", 1);

		fixture.AskAside($"block:claude:showBlock:{Chart("p95")}");
		var row = await fixture.WaitForMessageAsync(message => message.ConversationId is not null && message.ItemType == AgentDashboards.ItemType);

		Assert.Equal("Checkout rollout", fixture.Session.Output.View("fake", row.ConversationId).Dashboard("rollout")?.Summary);
		var member = await fixture.WaitForMessageAsync(message => message.ConversationId == row.ConversationId && message.ItemType == "block");
		Assert.Null(member.Text);
	}

	[Fact]
	public async Task UpdateBlock_RefusesADashboard() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart("p95")}", 1);

		await CallAsync(fixture, """block:claude:updateBlock:{"id":"p95","dashboard":{"id":"other","title":"Other"},"append":{"series":[{"name":"now","points":[[3,4]]}],"keep":5}}""", 2);

		var row = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:block-call-2" && message.Status == "failed");
		Assert.Contains("cannot move a block into a dashboard", row.Text, StringComparison.Ordinal);
	}

	private static List<AgentPaneMessage> Rows(AcpAgentSessionFixture fixture) =>
		[.. fixture.Session.Output.ReadHistory(null, null).Messages.Select(record => record.Message)];

	private static async Task CallAsync(AcpAgentSessionFixture fixture, string prompt, int turn) {
		fixture.Submit(prompt);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == turn.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}
}
