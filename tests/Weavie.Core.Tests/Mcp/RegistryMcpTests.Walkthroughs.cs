using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed partial class RegistryMcpTests {
	[Fact]
	public async Task WalkthroughToolsAreListedWithBlocks_AndStageACheckedWalkthrough() {
		using var store = NewStore();
		File.WriteAllText(Path.Combine(_dir.Path, "A.cs"), "class A {\n\tint b;\n}\n");
		var walkthroughs = new WalkthroughCapture(new LocalFileSystem(), _dir.Path, _ => false);
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], store, walkthroughs: walkthroughs);
		server.Start();
		await using var pane = server.CreateGrant(() => null);
		pane.Blocks.Bind(_ => null, _ => null);
		await using var other = server.CreateGrant(() => null);

		Assert.Contains("\"publishWalkthrough\"", await ListAsync(server, pane.Credential.Token), StringComparison.Ordinal);
		Assert.Contains("\"updateWalkthrough\"", await ListAsync(server, pane.Credential.Token), StringComparison.Ordinal);
		Assert.DoesNotContain("\"publishWalkthrough\"", await ListAsync(server, other.Credential.Token), StringComparison.Ordinal);
		using (var tools = JsonDocument.Parse(await ListAsync(server, pane.Credential.Token))) {
			var update = tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
				.Single(tool => tool.GetProperty("name").GetString() == "updateWalkthrough");
			Assert.Equal(["id", "change"], update.GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(name => name.GetString()));
		}

		const string publish = """
			{"id":"a","title":"How A works","problem":"p","gist":"g",
			 "stops":[{"id":"one","name":"One","title":"T","lead":"l","code":[{"path":"A.cs","from":1,"to":3,"notes":[{"line":2,"token":"int b","text":"a field"}]}]}]}
			""";
		using var shown = await CallToolAsync(server, pane.Credential.Token, "publishWalkthrough", publish);
		string text = ToolText(shown);
		Assert.StartsWith("weavie-block:", text, StringComparison.Ordinal);
		Assert.Contains("Walkthrough 'a' is shown to the user.", text, StringComparison.Ordinal);
		var operation = Assert.IsType<AgentBlockOperation.Show>(pane.Blocks.Take(text, null));
		Assert.Equal("A.cs", Assert.Single(operation.Block.Walkthrough!.Files).Path);
		Assert.Single(operation.Sources);

		using var wrong = await CallToolAsync(server, pane.Credential.Token, "publishWalkthrough", publish.Replace("int b", "int c", StringComparison.Ordinal));
		Assert.True(wrong.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Contains("Line 2 is \"int b;\"", ToolText(wrong), StringComparison.Ordinal);
		using var missing = await CallToolAsync(server, pane.Credential.Token, "updateWalkthrough", """{"id":"a","change":"c"}""");
		Assert.Equal("Walkthrough 'a' does not exist in this conversation. Publish it with publishWalkthrough.", ToolText(missing));
	}
}
