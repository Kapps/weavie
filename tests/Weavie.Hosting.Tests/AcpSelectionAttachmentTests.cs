using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>A selection the user attached reaches every agent as text and is journalled as its own row.</summary>
public sealed class AcpSelectionAttachmentTests {
	[Fact]
	public async Task SelectionReachesTheAgentAsTextAndIsJournalled() {
		await using var fixture = AcpAgentSessionFixture.CreateWithEmbeddedContext(false);
		await fixture.StartAsync();

		fixture.Session.Submit(new AgentTurnSubmission {
			Id = "submission",
			Text = "why does this spike?",
			Kind = AgentTurnSubmissionKind.Prompt,
			CommandName = string.Empty,
			Attachments = [new AgentInputAttachment.Selection { Id = "sel-1", Label = "p95 latency · 14:18–14:30", Text = "time,now\n14:18,318" }],
		});

		await fixture.WaitForMessageAsync(message => message.Type == "turn-completed");
		var row = Assert.Single(fixture.Messages, message => message.Type == "user-selection");
		Assert.Equal(("sel-1", "p95 latency · 14:18–14:30", "time,now\n14:18,318", "submitted"), (row.ItemId, row.Summary, row.Text, row.Status));
		string[] texts = [.. AcpPromptAssertions.Blocks(Assert.Single(AcpPromptAssertions.Read(fixture)))
			.Where(block => block.GetProperty("type").GetString() == "text")
			.Select(block => block.GetProperty("text").GetString()!)];
		Assert.Equal(["why does this spike?", "Attached selection (p95 latency · 14:18–14:30):\ntime,now\n14:18,318"], texts);
	}
}
