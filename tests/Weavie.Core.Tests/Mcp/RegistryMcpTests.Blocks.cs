using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed partial class RegistryMcpTests {
	[Fact]
	public async Task BlockToolsAreListedOnlyToAConversationThatCanShowThem_AndStageTheirOperation() {
		using var store = NewStore();
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store);
		server.Start();
		await using var pane = server.CreateGrant(() => null);
		pane.Blocks.Bind(_ => null, _ => null);
		await using var other = server.CreateGrant(() => null);

		Assert.Contains("\"showBlock\"", await ListAsync(server, pane.Credential.Token), StringComparison.Ordinal);
		Assert.DoesNotContain("\"showBlock\"", await ListAsync(server, other.Credential.Token), StringComparison.Ordinal);
		Assert.DoesNotContain("\"showBlock\"", await ListAsync(server, Token), StringComparison.Ordinal);
		using (var tools = JsonDocument.Parse(await ListAsync(server, pane.Credential.Token))) {
			var update = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
				.Single(tool => tool.GetProperty("name").GetString() == "updateBlock");
			var append = update.GetProperty("inputSchema").GetProperty("properties").GetProperty("append");
			Assert.Equal(["keep"], append.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
		}

		using var shown = await CallBlockAsync(server, pane.Credential.Token,
			"""{"id":"p95","title":"p95","table":{"columns":[{"name":"n","type":"number"}],"rows":[["7"]]}}""");
		string text = ToolText(shown);
		Assert.StartsWith("weavie-block:", text, StringComparison.Ordinal);
		var operation = Assert.IsType<AgentBlockOperation.Show>(pane.Blocks.Take(text, null));
		Assert.Equal(7, operation.Block.Table!.Rows[0][0].Number);

		using var unbound = await CallBlockAsync(server, other.Credential.Token, """{"id":"p95","title":"p95","table":{"columns":[],"rows":[]}}""");
		Assert.True(unbound.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		using var invalid = await CallBlockAsync(server, pane.Credential.Token, """{"id":"p95","title":"p95","chart":{"kind":"pie"}}""");
		Assert.Contains("Invalid showBlock arguments", ToolText(invalid), StringComparison.Ordinal);
	}

	[Fact]
	public async Task ShowBlock_SaysWhetherItCreatedOrJoinedADashboard() {
		using var store = NewStore();
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store);
		server.Start();
		await using var pane = server.CreateGrant(() => null);
		pane.Blocks.Bind(_ => null, id => id == "saved" ? new AgentPaneMessage { Type = "item-completed", ProviderId = "p", Summary = "Saved title" } : null);
		const string table = "\"table\":{\"columns\":[{\"name\":\"n\",\"type\":\"number\"}],\"rows\":[]}";

		using var created = await CallBlockAsync(server, pane.Credential.Token, $$"""{"id":"a","title":"a","dashboard":{"id":"rollout","title":"Rollout"},{{table}}}""");
		Assert.Contains("Created dashboard 'rollout' (\"Rollout\") with it.", ToolText(created), StringComparison.Ordinal);
		using var staged = await CallBlockAsync(server, pane.Credential.Token, $$"""{"id":"b","title":"b","dashboard":{"id":"rollout","title":"Other"},{{table}}}""");
		Assert.Contains("Added to dashboard 'rollout' (\"Rollout\").", ToolText(staged), StringComparison.Ordinal);
		using var saved = await CallBlockAsync(server, pane.Credential.Token, $$"""{"id":"c","title":"c","dashboard":{"id":"saved","title":"New"},{{table}}}""");
		Assert.Contains("Added to dashboard 'saved' (\"Saved title\").", ToolText(saved), StringComparison.Ordinal);
	}

	[Fact]
	public async Task OpenDashboard_OpensTheCallersOwnDashboard() {
		using var store = NewStore();
		var presenter = FakeDiffPresenter.AlwaysKeep();
		await using var server = TestMcp.Server(Token, presenter, [_dir.Path], store);
		server.Start();
		await using var pane = server.CreateGrant(() => null);
		pane.Blocks.Bind(_ => null, id => id == "rollout"
			? new AgentPaneMessage { Type = "item-completed", ProviderId = "p", ConversationId = "side", Summary = "Rollout" }
			: null);

		using var opened = await CallToolAsync(server, pane.Credential.Token, "openDashboard", """{"id":"rollout"}""");
		Assert.Equal("Dashboard 'rollout' (\"Rollout\") is open in an editor tab.", ToolText(opened));
		Assert.Equal(("dashboard:rollout@side", "dashboard", false), Assert.Single(presenter.OpenedTabs));
		using var focused = await CallToolAsync(server, pane.Credential.Token, "openDashboard", """{"id":"rollout","focus":"true"}""");
		Assert.True(presenter.OpenedTabs[^1].Focus);
		using var missing = await CallToolAsync(server, pane.Credential.Token, "openDashboard", """{"id":"other"}""");
		Assert.Contains("does not exist in this conversation", ToolText(missing), StringComparison.Ordinal);
		await using var unbound = server.CreateGrant(() => null);
		using var refused = await CallToolAsync(server, unbound.Credential.Token, "openDashboard", """{"id":"rollout"}""");
		Assert.True(refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Equal(2, presenter.OpenedTabs.Count);
	}

	private static async Task<string> ListAsync(McpServer server, string token) {
		using var response = await PostHttpAsync(server.Port, Request(1, "tools/list", "{}"), token);
		using var document = await TestMcp.ReadResponseAsync(response);
		return document.RootElement.GetRawText();
	}

	private static Task<JsonDocument> CallBlockAsync(McpServer server, string token, string arguments) =>
		CallToolAsync(server, token, "showBlock", arguments);

	private static async Task<JsonDocument> CallToolAsync(McpServer server, string token, string name, string arguments) {
		using var response = await PostHttpAsync(server.Port, Request(2, "tools/call", $$"""{"name":"{{name}}","arguments":{{arguments}}}"""), token);
		return await TestMcp.ReadResponseAsync(response);
	}
}
