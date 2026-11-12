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
		pane.Blocks.Bind(_ => null);
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

	private static async Task<string> ListAsync(McpServer server, string token) {
		using var response = await PostHttpAsync(server.Port, Request(1, "tools/list", "{}"), token);
		using var document = await TestMcp.ReadResponseAsync(response);
		return document.RootElement.GetRawText();
	}

	private static async Task<JsonDocument> CallBlockAsync(McpServer server, string token, string arguments) {
		using var response = await PostHttpAsync(server.Port, Request(2, "tools/call", $$"""{"name":"showBlock","arguments":{{arguments}}}"""), token);
		return await TestMcp.ReadResponseAsync(response);
	}
}
