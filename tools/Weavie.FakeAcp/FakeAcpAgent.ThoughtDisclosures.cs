using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private async Task<JsonNode> ThoughtDisclosuresAsync(CancellationToken ct) {
		VisibilityContent("agent_thought_chunk", "**Inspecting the transcript layout**\n\nThe first thought stays available for inspection.", "thought-first");
		VisibilityTool("build", "tool_call", "completed");
		VisibilityContent("agent_thought_chunk", "### Checking narrow panes and streaming content\n\n" + new string('x', 240) + "\n\n```text\n" + new string('y', 240) + "\n```", "thought-live");
		await HoldOnceAsync("thought-delta", ct).ConfigureAwait(false);
		VisibilityContent("agent_thought_chunk", "\n\nThe streamed continuation remains readable.", "thought-live");
		await HoldOnceAsync("thought-finish", ct).ConfigureAwait(false);
		Message("Thought disclosure checks complete.");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}
}
