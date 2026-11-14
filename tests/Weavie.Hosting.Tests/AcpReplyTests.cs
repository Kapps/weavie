using Weavie.Core.Agents;
using Xunit;
using static Weavie.Hosting.Tests.AcpLiveReplacementTests;

namespace Weavie.Hosting.Tests;

// The message an agent stops on is final; what it writes before going on to more work is not.
public sealed class AcpReplyTests {
	[Fact]
	public async Task EachMessageClaudeStopsOnIsFinalInsideATurnAndAfterIt() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.Submit("replies-claude");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Signal(fixture, "release-replies-late");
		await fixture.WaitForMessageAsync(message => message is { Text: "The late job finished.", Final: true });

		Assert.Equal([
			("Reading the settings first.", false),
			("Nothing is overridden, and the checks are running.", true),
			("Checks pass; reading the review next.", false),
			("The review is clean.", true),
			("Publishing the change.", true),
			("Everything is published.", true),
			("The late job finished.", true),
		], SavedReplies(fixture));
		Assert.Equal("1", fixture.Messages.Last(message => message.Text == "The late job finished.").TurnId);
	}

	[Fact]
	public async Task CodexFinalAnswerIsFinalWhereverItFallsAndATurnEndsOnAFinalMessage() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.Submit("replies-codex");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal([
			("Running the checks.", false),
			("The checks pass.", true),
			("Tidying is done.", true),
		], SavedReplies(fixture));
	}

	[Fact]
	public async Task AnAgentWithNoStopSignalEndsItsTurnOnAFinalMessage() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.Submit("activity-progress");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		var replies = SavedReplies(fixture);
		Assert.Equal(("The verification needs attention; its output is available in the activity row.", true), replies[^1]);
		Assert.All(replies[..^1], reply => Assert.False(reply.Final));
	}

	[Fact]
	public async Task AStopOnTheNoReplyMessageLeavesTheNoteBeforeItAProgressNote() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.Submit("replies-no-reply");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");

		Assert.Equal([("Refreshing the chart.", false), (null, false)], SavedReplies(fixture));
		Assert.Equal("item-retracted", Saved(fixture).Last(message => message.ItemType == "agentMessage").Type);
	}

	[Fact]
	public async Task AMessageTheNextPromptCutsShortIsFinalBeforeThatPromptsTurn() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		fixture.Submit("replies-cut-short");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Signal(fixture, "release-replies-cut");
		await fixture.WaitForMessageAsync(message => message.Text == "Still checking.");
		fixture.Submit("replies-silent");
		await fixture.WaitForMessageAsync(message => message is { Type: "turn-completed", TurnId: "2" });

		var saved = Saved(fixture);
		int reply = saved.FindLastIndex(message => message.ItemType == "agentMessage");
		Assert.Equal(("Still checking.", true), (saved[reply].Text, saved[reply].Final == true));
		Assert.True(reply < saved.FindIndex(message => message is { Type: "turn-started", TurnId: "2" }));
	}

	private static List<AgentPaneMessage> Saved(AcpAgentSessionFixture fixture) => [.. fixture.Sessions.ReadMessages("fake", fixture.Workspace)];

	// The agent messages a reloaded pane is given, from the saved journal.
	private static (string? Text, bool Final)[] SavedReplies(AcpAgentSessionFixture fixture) {
		var ledger = AgentPaneLedger.Of(Saved(fixture));
		return [.. Enumerable.Range(0, ledger.Count).Select(index => ledger.EntryAt(index).Message)
			.Where(message => message.ItemType == "agentMessage").Select(message => (message.Text, message.Final == true))];
	}
}
