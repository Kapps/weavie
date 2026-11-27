using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A walkthrough is a block whose cited code Weavie keeps; questions about it are anchored side conversations.</summary>
public sealed class AcpWalkthroughTests {
	private const string Code = "class A {\n\tint b;\n}\n";
	private const string Publish = """{"id":"a","title":"How A works","problem":"p","gist":"g","stops":[{"id":"one","name":"One","title":"T","lead":"l","code":[{"path":"A.cs","from":1,"to":3,"notes":[{"line":2,"token":"int b","text":"a field"}]}]}]}""";

	[Theory]
	[InlineData("claude")]
	[InlineData("codex")]
	public async Task PublishBecomesAWalkthroughRow_AndItsCodeIsKept(string relay) {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "A.cs"), Code);

		await fixture.TurnAsync($"block:{relay}:publishWalkthrough:{Publish}", 1);

		var row = Assert.Single(fixture.Rows(), message => message.ItemId == "tool:block-call-1");
		Assert.Equal(("item-completed", "block", "walkthrough", "How A works", "a"), (row.Type, row.ItemType, row.Category, row.Summary, row.BlockId));
		var walkthrough = fixture.Session.Output.Block("fake", null, "a")!.Walkthrough!;
		Assert.Equal(Code, fixture.Session.Source(null, "a", walkthrough.Files[0].Before));
	}

	[Fact]
	public async Task AnUpdateRetractsItsRow_AndKeepsTheFirstAndLatestCopies() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		string path = Path.Combine(fixture.Workspace, "A.cs");
		File.WriteAllText(path, Code);
		await fixture.TurnAsync($"block:claude:publishWalkthrough:{Publish}", 1);
		string edited = Code.Replace("int b", "long b", StringComparison.Ordinal);
		File.WriteAllText(path, edited);

		await fixture.TurnAsync("""block:codex:updateWalkthrough:{"id":"a","change":"b is a long.","stops":[{"id":"one","effect":"changed","note":"Was an int.","code":[{"path":"A.cs","from":1,"to":3,"notes":[{"line":2,"token":"long b","text":"a wider field"}]}]}]}""", 2);

		var walkthrough = fixture.Session.Output.Block("fake", null, "a")!.Walkthrough!;
		Assert.Equal(("changed", null), (walkthrough.Stops[0].Effect, walkthrough.Stops[0].Code[0].Notes![0].Was));
		var file = Assert.Single(walkthrough.Files);
		Assert.Equal((Code, edited), (fixture.Session.Source(null, "a", file.Before), fixture.Session.Source(null, "a", file.After!)));
		Assert.Equal("item-retracted", Assert.Single(fixture.Session.Output.ReadHistory(null, null).Messages, record => record.Message.ItemId == "tool:block-call-2").Message.Type);
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "notice");
	}

	[Fact]
	public async Task AQuestionAskedFromAWalkthroughCarriesItsAnchor() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "A.cs"), Code);
		await fixture.TurnAsync($"block:claude:publishWalkthrough:{Publish}", 1);

		var anchor = new AgentAsideAnchor(null, "a", "one/code/0/2", "int b");
		fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why a field?") with { Anchor = anchor });

		var started = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		Assert.Equal((anchor, "Why a field?"), (started.Anchor, started.Text));
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "side-conversation-started" && message.Anchor == anchor);
		Assert.Equal("That walkthrough is no longer shown.", Assert.Throws<InvalidOperationException>(() =>
			fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = new(null, "gone", "problem", "x") })).Message);
		Assert.Throws<ArgumentException>(() => fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = new(null, "a", "not a place", "x") }));
		Assert.Throws<ArgumentException>(() => fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = new(null, "a", "problem", "two\nlines") }));
	}

	[Fact]
	public async Task AQuestionCanBeAskedAboutAWalkthroughASideConversationPublished() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "A.cs"), Code);
		fixture.AskAside($"block:claude:publishWalkthrough:{Publish}");
		var row = await fixture.WaitForMessageAsync(message => message.Category == "walkthrough" && message.ConversationId is not null);

		fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = new(row.ConversationId, "a", "gist", "g") });

		await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started" && message.Anchor?.ConversationId == row.ConversationId);
	}

	[Fact]
	public async Task StopAsideStopsOnlyThatSideConversation() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		fixture.Submit("hold");
		await fixture.WaitForHeldToolAsync(message => message.ConversationId is null);
		fixture.AskAside("hold");
		var side = await fixture.WaitForHeldToolAsync(message => message.ConversationId is not null);

		fixture.Session.StopAside(side.ConversationId!);

		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId == side.ConversationId && message.Status == "cancelled");
		Assert.DoesNotContain(fixture.Messages, message => message.Type == "turn-completed" && message.ConversationId is null);
		fixture.Session.StopAside(side.ConversationId!);
		fixture.Session.StopAside("missing");
	}

	[Fact]
	public async Task AWalkthroughsCodeLeavesWithIt_OnClearAndRewind_AndComesBackOnRestore() {
		await using var fixture = await AcpAgentSessionFixture.StartedAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "A.cs"), Code);
		await fixture.TurnAsync($"block:claude:publishWalkthrough:{Publish}", 1);
		string hash = fixture.Session.Output.Block("fake", null, "a")!.Walkthrough!.Files[0].Before;

		fixture.Session.Restore();
		Assert.Equal(Code, fixture.Session.Source(null, "a", hash));
		fixture.Session.SetCardCollapsed(new(null, null, "a"), collapsed: true);
		var collapsed = await fixture.WaitForMessageAsync(message => message.Type == "block-collapsed");
		Assert.Equal(("a", "collapsed"), (collapsed.BlockId, collapsed.Status));
		Assert.Throws<InvalidOperationException>(() => fixture.Session.SetCardCollapsed(new(null, null, "gone"), collapsed: true));

		await fixture.TurnAsync("alpha", 2);
		await fixture.WaitForControlsAsync(state => state.Rewindable);
		await fixture.Session.RewindBeforeAsync("1");
		await fixture.WaitForSnapshotAsync();
		Assert.Throws<KeyNotFoundException>(() => fixture.Session.Source(null, "a", hash));

		await fixture.TurnAsync($"block:claude:publishWalkthrough:{Publish}", 1);
		fixture.Session.StartNewConversation();
		await fixture.WaitForMessageAsync(message => message.Type == "transcript-reset");
		Assert.Throws<KeyNotFoundException>(() => fixture.Session.Source(null, "a", hash));
	}

	[Fact]
	public async Task AQuestionsAnchorIsRestoredWithItsRow() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		await fixture.StartAsync();
		File.WriteAllText(Path.Combine(fixture.Workspace, "A.cs"), Code);
		await fixture.TurnAsync($"block:claude:publishWalkthrough:{Publish}", 1);
		var anchor = new AgentAsideAnchor(null, "a", "gist", "g");
		fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = anchor });
		var started = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed" && message.ConversationId == started.ConversationId);

		fixture.Session.Restore();

		Assert.Contains(fixture.Session.Output.ReadHistory(null, null).Messages, record => record.Message.Anchor == anchor);
	}
}
