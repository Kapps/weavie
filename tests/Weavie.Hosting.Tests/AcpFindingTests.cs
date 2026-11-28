using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A pinned finding is a block the board lists, and the settings' sentences ride on the user's prompt.</summary>
public sealed class AcpFindingTests {
	private const string Pin = """{"id":"cause","title":"The review opens with no merge check.","detail":"`ReadOperationAsync` has one caller."}""";
	private const string Repin = """{"id":"cause","title":"The review checks for a merge, but too late."}""";

	[Theory]
	[InlineData("claude")]
	[InlineData("codex")]
	public async Task TheFirstPin_BecomesAFindingRow_ThatWasNeverATool(string relay) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await fixture.TurnAsync($"block:{relay}:pinFinding:{Pin}", 1);

		var row = Assert.Single(fixture.Rows(), message => message.ItemId == "tool:block-call-1");
		Assert.Equal(("item-completed", "block", "finding", "The review opens with no merge check.", "cause"),
			(row.Type, row.ItemType, row.Category, row.Summary, row.BlockId));
		Assert.DoesNotContain(fixture.Messages, message => message.ItemId == "tool:block-call-1" && message.ItemType == "tool");
		var block = fixture.Session.Output.Block("fake", null, "cause")!;
		Assert.Equal(("`ReadOperationAsync` has one caller.", null), (block.Finding!.Detail, block.UpdatedAtMs));
		Assert.DoesNotContain(fixture.Rows(), message => message.Type == "notice");
	}

	[Fact]
	public async Task PinningAnIdAgain_ReplacesTheFinding_AndLeavesNoRow() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await fixture.TurnAsync($"block:claude:pinFinding:{Pin}", 1);

		await fixture.TurnAsync($"block:claude:pinFinding:{Repin}", 2);

		var block = fixture.Session.Output.Block("fake", null, "cause")!;
		Assert.Equal(("The review checks for a merge, but too late.", null), (block.Title, block.Finding!.Detail));
		Assert.NotNull(block.UpdatedAtMs);
		Assert.Equal("item-retracted", Assert.Single(fixture.Rows(), message => message.ItemId == "tool:block-call-2").Type);
		Assert.Single(fixture.Rows(), message => message.ItemType == "block");
	}

	[Fact]
	public async Task ParallelPinsOfOneId_PinThenReplace() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await fixture.TurnAsync($"block-parallel:claude:pinFinding:{Pin}\n{Repin}", 1);

		Assert.Single(fixture.Rows(), message => message.ItemType == "block");
		Assert.NotNull(fixture.Session.Output.Block("fake", null, "cause")!.UpdatedAtMs);
		Assert.DoesNotContain(fixture.Rows(), message => message.Type == "notice");
	}

	[Fact]
	public async Task AFindingAndAChart_CannotTakeEachOthersId() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		await fixture.TurnAsync($"block:claude:pinFinding:{Pin}", 1);

		await fixture.TurnAsync("""block:claude:updateBlock:{"id":"cause","title":"Renamed"}""", 2);

		var refused = Assert.Single(fixture.Rows(), message => message.ItemId == "tool:block-call-2");
		Assert.Equal("failed", refused.Status);
		Assert.Contains("change it with pinFinding", refused.Text, StringComparison.Ordinal);
		Assert.Equal("The review opens with no merge check.", fixture.Session.Output.Block("fake", null, "cause")!.Title);
	}

	[Fact]
	public async Task ASubagentsPin_IsDiscardedWithANotice() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();

		await fixture.TurnAsync($"block-subagent:pinFinding:{Pin}", 1);

		await fixture.WaitForMessageAsync(message => message.Type == "notice"
			&& message.Text == "Blocks from subagents are not shown, so finding 'cause' was not pinned.");
		Assert.Null(fixture.Session.Output.Block("fake", null, "cause"));
	}

	[Theory]
	[InlineData(true, false, AgentPromptPreferences.PinAsk)]
	[InlineData(false, true, AgentPromptPreferences.AdhdReader)]
	[InlineData(true, true, AgentPromptPreferences.PinAsk + " " + AgentPromptPreferences.AdhdReader)]
	[InlineData(false, false, "")]
	public async Task APrompt_CarriesTheEnabledSentences_AndTheTranscriptOnlyWhatWasTyped(bool board, bool reader, string expected) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		Set(fixture, board, reader);

		fixture.Submit("preferences");

		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "preferences:" + expected);
		Assert.Equal("preferences", Assert.Single(fixture.Messages, message => message.Type == "user-message").Text);
	}

	[Fact]
	public async Task ASideConversation_IsNotAskedToPin() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		Set(fixture, board: true, reader: true);

		fixture.AskAside("preferences");

		await fixture.WaitForMessageAsync(message => message.ConversationId is not null
			&& message.Type == "item-completed" && message.Text == "preferences:" + AgentPromptPreferences.AdhdReader);
	}

	[Fact]
	public async Task ASlashCommandAndAnotherSessionsMessage_CarryNoSentences() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		Set(fixture, board: true, reader: true);

		fixture.SubmitCommand("review", "/review focus on tests");
		await fixture.WaitForMessageAsync(message => message.Text == "review command: focus on tests");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		fixture.Session.Submit(AgentTurnSubmission.Prompt("ping") with { Sender = new AgentMessageSender("feat/other", "Other") });
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: ping");

		Assert.Equal(2, AcpPromptAssertions.Read(fixture).Length);
		Assert.DoesNotContain(AcpPromptAssertions.Read(fixture).SelectMany(request => AcpPromptAssertions.Blocks(request).ToArray()), block =>
			block.GetProperty("type").GetString() == "text" && block.GetProperty("text").GetString()!.Contains("ADHD", StringComparison.Ordinal));
	}

	private static void Set(AcpAgentSessionFixture fixture, bool board, bool reader) {
		fixture.Settings.Set(AgentSettings.FindingsBoard, JsonSerializer.SerializeToElement(board));
		fixture.Settings.Set(AgentSettings.AdhdReader, JsonSerializer.SerializeToElement(reader));
	}
}
