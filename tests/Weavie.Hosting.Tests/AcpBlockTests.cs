using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A block tool call becomes a block on its own row, however the agent relays the tool's result.</summary>
public sealed class AcpBlockTests {
	private const string Chart = """{"id":"p95","title":"p95 latency","chart":{"kind":"line","x":{"type":"time"},"series":[{"name":"now","points":[[1794490680000,318],["2026-11-12T14:19:00Z",344]]}]}}""";
	private const string Append = """{"id":"p95","append":{"series":[{"name":"now","points":[[1794490800000,98765]]}],"keep":2}}""";

	[Theory]
	[InlineData("claude")]
	[InlineData("codex")]
	[InlineData("escaped")]
	[InlineData("content")]
	[InlineData("fenced")]
	[InlineData("truncated")]
	[InlineData("late")]
	[InlineData("repeat")]
	[InlineData("first")]
	public async Task ShowBlock_BecomesItsOwnRow_ForEveryRelay(string relay) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block:{relay}:showBlock:{Chart}");

		var rows = fixture.Session.Output.ReadHistory(null, null).Messages.Select(record => record.Message).ToList();
		var row = Assert.Single(rows, message => message.ItemId == "tool:block-call-1");
		Assert.Equal(("item-completed", "block", "chart", "p95 latency", "p95"), (row.Type, row.ItemType, row.Category, row.Summary, row.BlockId));
		Assert.Null(row.Text);
		var block = fixture.Session.Output.Block("fake", null, "p95")!;
		Assert.Equal(DateTimeOffset.Parse("2026-11-12T14:19:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), block.Chart!.Series[0].Points[1][0].Number);
		Assert.DoesNotContain(rows, message => message.Type == "notice");
	}

	[Theory]
	[InlineData("none")]
	[InlineData("noresult")]
	[InlineData("altered")]
	public async Task ShowBlock_WithoutItsTokenOnTheRow_SaysTheBlockWasNotShown(string relay) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block:{relay}:showBlock:{Chart}");

		await fixture.WaitForMessageAsync(message => message.Type == "notice"
			&& message.Text == "Fake ACP did not report the result of showBlock, so block 'p95' was not shown.");
		Assert.Null(fixture.Session.Output.Block("fake", null, "p95"));
		Assert.DoesNotContain(fixture.Messages, message => message.ItemType == "block");
	}

	[Fact]
	public async Task UpdateBlock_ChangesTheBlockInPlace_AndLeavesNoRowOrDataInTheJournal() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");

		await CallAsync(fixture, $"block:codex:updateBlock:{Append}", 2);

		var block = fixture.Session.Output.Block("fake", null, "p95")!;
		Assert.Equal([344, 98765], block.Chart!.Series[0].Points.Select(point => point[1].Number!.Value));
		Assert.NotNull(block.UpdatedAtMs);
		var update = Assert.Single(fixture.Session.Output.ReadHistory(null, null).Messages, record => record.Message.ItemId == "tool:block-call-2");
		Assert.Equal("item-retracted", update.Message.Type);
		Assert.DoesNotContain(fixture.Messages, message => message.ItemId == "tool:block-call-2" && message.Type == "item-started");
		Assert.DoesNotContain(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.ItemId == "tool:block-call-2"
			&& (message.Text ?? "").Contains("98765", StringComparison.Ordinal));
		Assert.Equal(98765, fixture.PublishedBlocks[^1].State.Block.Chart!.Series[0].Points[1][1].Number);
		var saved = Assert.Single(fixture.Sessions.ReadBlocks("fake", fixture.Workspace, fixture.Sessions.ReadMessages("fake", fixture.Workspace)));
		Assert.Equal(Json(block), Json(saved.Block));
	}

	[Fact]
	public async Task UpdateBlock_UnderATitleThatDoesNotNameIt_StillAppliesAndRemovesItsRow() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");

		await CallAsync(fixture, $"block:untitled:updateBlock:{Append}", 2);

		Assert.Equal(98765, fixture.Session.Output.Block("fake", null, "p95")!.Chart!.Series[0].Points[1][1].Number);
		Assert.Contains(fixture.Messages, message => message.ItemId == "tool:block-call-2" && message.Type == "item-started");
		Assert.Equal("item-retracted", Assert.Single(fixture.Session.Output.ReadHistory(null, null).Messages, record => record.Message.ItemId == "tool:block-call-2").Message.Type);
	}

	[Theory]
	[InlineData("[[weavie:no-reply]]")]
	[InlineData("[[wea|vie:no-|reply]]|\n")]
	public async Task AReplyThatIsOnlyTheNoReplyMessage_IsNeverShown_AndLeavesItsMessageIdForRewind(string chunks) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, "say:" + chunks);

		var reply = Assert.Single(fixture.Messages, message => message.ItemType == "agentMessage");
		Assert.Equal(("item-retracted", (string?)null), (reply.Type, reply.Text));
		Assert.NotNull(reply.MessageId);
		var saved = Assert.Single(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.ItemType == "agentMessage");
		Assert.Equal(("item-retracted", (string?)null, reply.MessageId), (saved.Type, saved.Text, saved.MessageId));
	}

	[Theory]
	[InlineData("[[wea|ther]] is fine", "[[weather]] is fine")]
	[InlineData("[[weavie:no-reply]]| but read this", "[[weavie:no-reply]] but read this")]
	[InlineData("[[weavie:no", "[[weavie:no")]
	public async Task AReplyThatOnlyStartsLikeTheNoReplyMessage_IsShownWhole(string chunks, string text) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, "say:" + chunks);

		var reply = Assert.Single(fixture.Session.Output.ReadHistory(null, null).Messages, record => record.Message.ItemType == "agentMessage");
		Assert.Equal(("item-completed", text), (reply.Message.Type, reply.Message.Text));
	}

	[Fact]
	public async Task ParallelCalls_EachClaimTheirOwnRow_WhateverOrderTheirResultsArrive() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, "block-parallel:claude:showBlock:" + Chart.Replace("p95", "a", StringComparison.Ordinal) + "\n" + Chart.Replace("p95", "b", StringComparison.Ordinal));

		var rows = fixture.Session.Output.ReadHistory(null, null).Messages.Select(record => record.Message).ToList();
		Assert.Equal("a", Assert.Single(rows, message => message.ItemId == "tool:block-call-1").BlockId);
		Assert.Equal("b", Assert.Single(rows, message => message.ItemId == "tool:block-call-2").BlockId);
	}

	[Fact]
	public async Task SubagentCalls_AreNotShown_AndSaySo() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block-subagent:showBlock:{Chart}");

		await fixture.WaitForMessageAsync(message => message.Type == "notice"
			&& message.Text == "Blocks from subagents are not shown, so block 'p95' was not shown.");
		Assert.Null(fixture.Session.Output.Block("fake", null, "p95"));
	}

	[Fact]
	public async Task RefusedCalls_FailTheirRow_WithTheReason() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block:claude:updateBlock:{Append}");

		var row = await fixture.WaitForMessageAsync(message => message.ItemId == "tool:block-call-1" && message.Status == "failed");
		Assert.Contains("does not exist", row.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Clear_DeletesTheConversationsBlocks() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");

		fixture.Session.StartNewConversation();
		await fixture.WaitForMessageAsync(message => message.Type == "transcript-reset");

		Assert.Null(fixture.Session.Output.Block("fake", null, "p95"));
		Assert.Empty(StoredBlocks(fixture));
	}

	[Fact]
	public async Task Rewind_KeepsEarlierBlocks_AndDeletesLaterOnes() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart.Replace("p95", "kept", StringComparison.Ordinal)}");
		await CallAsync(fixture, "alpha", 2);
		await CallAsync(fixture, $"block:claude:showBlock:{Chart.Replace("p95", "dropped", StringComparison.Ordinal)}", 3);
		await fixture.WaitForControlsAsync(state => state.Rewindable);

		await fixture.Session.RewindBeforeAsync("3");
		await fixture.WaitForSnapshotAsync();

		Assert.NotNull(fixture.Session.Output.Block("fake", null, "kept"));
		Assert.Null(fixture.Session.Output.Block("fake", null, "dropped"));
		Assert.Equal(["kept"], StoredBlocks(fixture));
	}

	[Fact]
	public async Task ARefusedTurn_DeletesTheBlockItShowed() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block-refused:first:showBlock:{Chart}");

		Assert.Null(fixture.Session.Output.Block("fake", null, "p95"));
		Assert.Empty(StoredBlocks(fixture));
	}

	[Fact]
	public async Task Show_AddsNoJournalEventBeyondTheCallsOwn_AndRestoresFromTheStore() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");

		var events = fixture.Sessions.ReadMessages("fake", fixture.Workspace).Where(message => message.ItemId == "tool:block-call-1").ToList();
		Assert.Equal(["item-started", "item-started", "item-completed"], events.Select(message => message.Type));
		Assert.Equal("block", events[^1].ItemType);
		fixture.Session.Restore();
		Assert.Equal("p95 latency", fixture.Session.Output.Block("fake", null, "p95")!.Title);
	}

	[Fact]
	public async Task Appends_ApplyInTheOrderTheirRowsAreClaimed() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");
		static string Point(int value) => $$$"""{"id":"p95","append":{"series":[{"name":"now","points":[[{{{1794490800000 + value}}},{{{value}}}]]}],"keep":10}}""";

		await CallAsync(fixture, $"block-parallel:codex:updateBlock:{Point(1)}\n{Point(2)}", 2);

		Assert.Equal([318, 344, 2, 1], fixture.Session.Output.Block("fake", null, "p95")!.Chart!.Series[0].Points.Select(point => point[1].Number!.Value));
	}

	[Fact]
	public async Task ASideConversationsBlocks_AreItsOwn() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await CallAsync(fixture, $"block:claude:showBlock:{Chart}");

		fixture.AskAside($"block:claude:showBlock:{Chart.Replace("p95 latency", "side latency", StringComparison.Ordinal)}");
		var row = await fixture.WaitForMessageAsync(message => message.ConversationId is not null && message.ItemType == "block");

		Assert.Equal("p95 latency", fixture.Session.Output.Block("fake", null, "p95")!.Title);
		Assert.Equal("side latency", fixture.Session.Output.Block("fake", row.ConversationId, "p95")!.Title);
	}

	// The table itself, not a read filtered by the rows that are left.
	private static List<string> StoredBlocks(AcpAgentSessionFixture fixture) {
		using var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={fixture.Sessions.FilePath};Pooling=False");
		database.Open();
		using var command = database.CreateCommand();
		command.CommandText = "SELECT id FROM blocks ORDER BY id";
		using var reader = command.ExecuteReader();
		var ids = new List<string>();
		while (reader.Read()) ids.Add(reader.GetString(0));
		return ids;
	}

	private static string Json(AgentBlock block) => System.Text.Json.JsonSerializer.Serialize(block, AgentBlockJson.Default.AgentBlock);

	private static Task CallAsync(AcpAgentSessionFixture fixture, string prompt) => CallAsync(fixture, prompt, 1);

	private static async Task CallAsync(AcpAgentSessionFixture fixture, string prompt, int turn) {
		fixture.Submit(prompt);
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.TurnId == turn.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}
}
