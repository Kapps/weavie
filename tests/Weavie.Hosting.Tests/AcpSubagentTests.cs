using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using Xunit;
using static Weavie.Hosting.Tests.AcpLiveReplacementTests;

namespace Weavie.Hosting.Tests;

// A subagent is an owned read-only conversation adopted from its parent's announcement and rendered as a card.
public sealed class AcpSubagentTests {
	[Fact]
	public async Task SpawnRendersAReadOnlyCardThatCompletesAndRetires() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("subagent");

		var completed = await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is not null);
		var marker = Assert.Single(fixture.Messages, message => message.Type == "subagent-started");
		Assert.Equal((completed.ConversationId, "1", "Explore", "Count files", false), (marker.ConversationId, marker.AnchorTurnId, marker.Summary, marker.Text, marker.IsPrimaryThread));
		Assert.Equal("completed", completed.Status);
		Assert.NotNull(completed.CompletedAtMs);
		Assert.Contains(fixture.Messages, message => message.ConversationId == marker.ConversationId && message.ItemType == "tool");
		Assert.Contains(fixture.Messages, message => message.ConversationId == marker.ConversationId && message.Text == "Counted 3 files");
		await fixture.WaitForMessageAsync(message => message.Text == "subagent turn done" && message.Type == "item-completed");
		Assert.DoesNotContain(fixture.Messages, message => message.ItemId is "tool:spawn-agent" or "tool:toolu_hidden_agent" || message.Type == "error");
		await fixture.Events.WaitForAsync(value => value is AgentConversationRemoved removed && removed.ConversationId == marker.ConversationId);
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "subagent-started");
		Assert.Equal("", Assert.Single(fixture.Sessions.ReadConversations("fake", fixture.Workspace)).ConversationId);
		var item = Assert.Single(fixture.Session.BackgroundWork);
		Assert.Equal((marker.ConversationId, AgentBackgroundKind.Subagent, AgentBackgroundState.Completed), (item.Id, item.Kind, item.State));

		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: hello" && message.Type == "item-completed");
		Assert.Empty(fixture.Session.BackgroundWork);
	}

	[Fact]
	public async Task ChildApprovalIsAnsweredThroughItsNamespacedRequest() {
		await using var fixture = await StartedAsync(allowAllPermissions: false);
		fixture.Submit("subagent-permission");

		var request = await fixture.WaitForMessageAsync(message => message.Type == "approval-requested");
		Assert.StartsWith(request.ConversationId + ":", request.RequestId, StringComparison.Ordinal);
		Assert.Equal(SessionStatus.NeedsInput, fixture.Events.Status.Status);
		fixture.Session.ResolvePermission(request.RequestId!, "allow");

		await fixture.WaitForMessageAsync(message => message.ConversationId == request.ConversationId && message.Text == "child permission: allow");
		await fixture.WaitForMessageAsync(message => message.ConversationId == request.ConversationId && message.Type == "turn-completed");
	}

	[Fact]
	public async Task NestedSubagentRendersAsItsOwnCardNamingItsParent() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("subagent-nested");

		await fixture.WaitForMessageAsync(message => message.Text == "subagent turn done" && message.Type == "item-completed");
		var markers = fixture.Messages.Where(message => message.Type == "subagent-started").ToArray();
		Assert.Equal(2, markers.Length);
		Assert.Null(markers[0].ParentItemId);
		Assert.Equal(markers[0].ConversationId, markers[1].ParentItemId);
		Assert.All(markers, marker => Assert.Equal("1", marker.AnchorTurnId));
		Assert.All(markers, marker => Assert.Contains(fixture.Messages, message =>
			message.Type == "turn-completed" && message.ConversationId == marker.ConversationId && message.Status == "completed"));
	}

	[Fact]
	public async Task RunningSubagentKeepsTheSessionWaitingAfterItsTurnEnds() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		var marker = await StartHeldSubagentAsync(fixture, "1");

		Assert.Equal(SessionStatus.Waiting, fixture.Events.Status.Status);
		Assert.True(Assert.Single(fixture.Session.BackgroundWork).Running);
		Signal(fixture, "release-subagent");

		await fixture.WaitForMessageAsync(message => message.ConversationId == marker.ConversationId && message.Type == "turn-completed");
		await fixture.Events.WaitForAsync(value => value is AgentBackgroundChanged { Running: false });
		Assert.Equal(SessionStatus.Idle, fixture.Events.Status.Status);
	}

	[Fact]
	public async Task RestartCancelsAndCrashFailsRunningSubagents() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		var restarted = await StartHeldSubagentAsync(fixture, "1");
		fixture.Session.Restart();
		var cancelled = await fixture.WaitForMessageAsync(message => message.ConversationId == restarted.ConversationId && message.Type == "turn-completed");
		Assert.Equal("cancelled", cancelled.Status);
		await fixture.WaitForControlsAsync(state => state.Ready);

		var crashed = await StartHeldSubagentAsync(fixture, "2");
		fixture.Submit("crash-when-released");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is null && message.TurnId == "3");
		Signal(fixture, "release-crash");
		var failed = await fixture.WaitForMessageAsync(message => message.ConversationId == crashed.ConversationId && message.Type == "turn-completed");
		Assert.Equal("failed", failed.Status);
		Assert.Equal(AgentBackgroundState.Failed, Assert.Single(fixture.Session.BackgroundWork, item => item.Id == crashed.ConversationId).State);
	}

	[Fact]
	public async Task ReplayedSubagentsAreNeitherDisplayedNorFaulting() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: hello");
		Signal(fixture, "replay-subagents");

		fixture.AskAside("side question");

		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: side question");
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "subagent-started" or "error");
		Assert.DoesNotContain(fixture.Messages, message => message.Text?.Contains("replayed", StringComparison.Ordinal) == true);
		Assert.Empty(fixture.Session.BackgroundWork);
	}

	[Fact]
	public async Task ForkLoadAnnouncingALiveSubagentLeavesItsCardAlone() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		var marker = await StartHeldSubagentAsync(fixture, "1");
		Signal(fixture, "fork-announces-live-subagent");

		fixture.AskAside("side question");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: side question");
		Assert.True(Assert.Single(fixture.Session.BackgroundWork).Running);
		Signal(fixture, "release-subagent");

		var completed = await fixture.WaitForMessageAsync(message => message.ConversationId == marker.ConversationId && message.Type == "turn-completed");
		Assert.Equal("completed", completed.Status);
		Assert.Contains(fixture.Messages, message => message.ConversationId == marker.ConversationId && message.Text == "held subagent finished");
		Assert.Single(fixture.Messages, message => message.Type == "subagent-started");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "error");
	}

	[Fact]
	public async Task SubagentAnnouncedOnARetiredConversationIsDropped() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("hello");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		Signal(fixture, "spawn-after-close");

		fixture.Session.StartNewConversation();
		await WaitForCloseAsync(fixture, "fake-session");

		await AssertSuccessorAnswersAsync(fixture, "fake-session-2");
		Assert.DoesNotContain(fixture.Messages, message => message.Type is "subagent-started" or "error");
		Assert.Equal(1, ProcessStarts(fixture));
	}

	[Fact]
	public async Task ClearWithARunningSubagentRestartsTheProcessSoTheWorkStops() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		var marker = await StartHeldSubagentAsync(fixture, "1");

		fixture.Session.StartNewConversation();

		await fixture.WaitForControlsAsync(state => state.Ready);
		Assert.Equal(2, ProcessStarts(fixture));
		Assert.Contains(fixture.Messages, message => message.ConversationId == marker.ConversationId && message.Status == "cancelled");
		Assert.Empty(fixture.Session.BackgroundWork);
	}

	[Fact]
	public async Task ASecondLiveOwnerForOneSubagentFailsTheRuntime() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("subagent-duplicate");

		var error = await fixture.WaitForMessageAsync(message => message.Type == "error");
		Assert.Contains("already has an owner", error.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RewindDropsSubagentCardsFromTheCut() {
		await using var fixture = await StartedAsync(allowAllPermissions: true);
		fixture.Submit("alpha");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "echo: alpha");
		fixture.Submit("subagent");
		await fixture.WaitForMessageAsync(message => message.Type == "item-completed" && message.Text == "subagent turn done");
		await fixture.WaitForControlsAsync(state => state.Rewindable);

		await fixture.Session.RewindBeforeAsync("2");

		var snapshot = await fixture.WaitForSnapshotAsync();
		Assert.DoesNotContain(snapshot, message => message.ConversationId is not null);
		Assert.DoesNotContain(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.ConversationId is not null);
	}

	private static async Task<AcpAgentSessionFixture> StartedAsync(bool allowAllPermissions) {
		var fixture = AcpAgentSessionFixture.Create(allowAllPermissions, persistedSessionId: null);
		await fixture.StartAsync();
		return fixture;
	}

	private static async Task<AgentPaneMessage> StartHeldSubagentAsync(AcpAgentSessionFixture fixture, string turnId) {
		fixture.Submit("subagent-held");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId is null && message.TurnId == turnId);
		return fixture.Messages.Last(message => message.Type == "subagent-started");
	}
}
