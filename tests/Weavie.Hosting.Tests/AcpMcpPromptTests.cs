using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpMcpPromptTests {
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CatalogIsAvailableBeforeFirstTurnAndInvocationUsesItsInstructions(bool embeddedContext) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(embeddedContext);
		foreach (var prompt in McpPromptCatalog.All) {
			var entry = Assert.Single(fixture.Session.ControlState.Slash, entry => entry.Name == prompt.Name);
			Assert.Equal(AgentSlashEntryKind.McpPrompt, entry.Kind);
			Assert.Equal(prompt.Description, entry.Description);
		}
		await fixture.StartAsync();
		foreach (var prompt in McpPromptCatalog.All) {
			string invocation = $"/{prompt.Name} invented example";
			fixture.Session.Submit(Submission(prompt.Name, invocation));
			await fixture.WaitForMessageAsync(message => message.Type == "item-completed"
				&& message.Text == Acknowledgment(prompt.Name));
			var request = AcpPromptAssertions.Read(fixture).Last();
			var blocks = AcpPromptAssertions.Blocks(request).ToArray();
			Assert.Equal(prompt.Text, blocks[0].GetProperty("text").GetString());
			Assert.Equal("invented example", blocks[1].GetProperty("text").GetString());
			Assert.Contains(fixture.Messages, message => message.Type == "user-message" && message.Text == invocation);
			Assert.DoesNotContain(fixture.Messages, message => message.Type == "user-message" && message.Text == prompt.Text);
		}
	}

	[Theory]
	[InlineData("unknown", "/unknown")]
	[InlineData("../report-weavie-bug", "/../report-weavie-bug")]
	[InlineData("report-weavie-bug", "/request-weavie-feature")]
	[InlineData("report-weavie-bug", "/report-weavie-bug-extra")]
	public async Task RejectsUnknownOrMismatchedPromptIdentity(string name, string text) {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(false);
		Assert.Throws<InvalidOperationException>(() => fixture.Session.Submit(Submission(name, text)));
	}

	[Fact]
	public async Task SelectedWorkflowWaitsForItsOwnTurnWhileOrdinaryPromptsCanSteer() {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(false);
		await fixture.StartAsync();
		fixture.Submit("hold");
		await Wait.UntilAsync(() => File.Exists(Path.Combine(fixture.Workspace, "hold-started")));
		fixture.Session.Submit(Submission("report-weavie-bug", "/report-weavie-bug"));
		await fixture.WaitForQueueAsync(queue => queue.Count == 1 && queue[0].Kind == AgentTurnSubmissionKind.McpPrompt);
		fixture.Submit("new direction");
		await fixture.WaitForMessageAsync(message => message.Type == "user-steer" && message.Text == "new direction");
		await fixture.WaitForMessageAsync(message => message.Text == Acknowledgment("report-weavie-bug"));
		Assert.DoesNotContain(AcpPromptAssertions.Read(fixture), request =>
			request.GetProperty("method").GetString() == "_session/steering"
			&& AcpPromptAssertions.Blocks(request).Any(block => block.TryGetProperty("text", out var text)
				&& text.GetString() == IssueReportingPrompts.ReportBug.Text));
	}

	private static string Acknowledgment(string name) =>
		$"Received Weavie MCP prompt: {name}. Deterministic fixture acknowledgment only; no action or report submitted.";

	private static AgentTurnSubmission Submission(string name, string text) => new() {
		Id = Guid.NewGuid().ToString("N"),
		Text = text,
		Kind = AgentTurnSubmissionKind.McpPrompt,
		CommandName = name,
		Attachments = [],
	};
}
