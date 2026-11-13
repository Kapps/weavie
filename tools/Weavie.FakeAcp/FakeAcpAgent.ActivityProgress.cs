using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private async Task<JsonNode> ActivityProgressAsync(bool longHistory, CancellationToken ct) {
		for (int index = 1; index <= (longHistory ? 24 : 6); index++) {
			Message($"Update {index}: the implementation details remain readable.");
			Update(Tool($"progress-{index}", $"Inspect section {index}", "completed"));
		}
		Message("Checking the final verification result.");
		Update(Tool("progress-check", "Verify application with the complete integration suite before publishing the implementation and reviewing every diagnostic", "in_progress"));
		await HoldOnceAsync("activity-progress", ct).ConfigureAwait(false);
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = "progress-check",
			["status"] = "failed",
			["content"] = new JsonArray(new JsonObject {
				["type"] = "content",
				["content"] = Text("Verification output: one assertion needs attention."),
			}),
		});
		Message("The verification needs attention; its output is available in the activity row.");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}
}
