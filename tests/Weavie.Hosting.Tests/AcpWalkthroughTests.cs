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

		fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why a field?") with { Anchor = new("a", "one/code/0/2", "int b") });

		var started = await fixture.WaitForMessageAsync(message => message.Type == "side-conversation-started");
		Assert.Equal(("a", "one/code/0/2", "int b", "Why a field?"), (started.BlockId, started.Anchor, started.Summary, started.Text));
		Assert.Contains(fixture.Sessions.ReadMessages("fake", fixture.Workspace), message => message.Type == "side-conversation-started" && message.Anchor == "one/code/0/2");
		Assert.Equal("That walkthrough is no longer shown.", Assert.Throws<InvalidOperationException>(() =>
			fixture.Session.AskAside(AgentTurnSubmission.Prompt("Why?") with { Anchor = new("gone", "problem", "x") })).Message);
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
}
