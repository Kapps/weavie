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

	private async Task<JsonNode> ActivityMixedAsync(CancellationToken ct) {
		Update(Tool("preflight", "Inspect repository", "completed"));
		await HoldOnceAsync("activity-question", ct).ConfigureAwait(false);
		await QuestionAsync(ct).ConfigureAwait(false);
		string child = NextSubagent();
		SpawnSubagent(_sessionId!, child, "Explore", "Review build output");
		UpdateOn(child, MessageUpdate("Inspecting project files."));
		UpdateOn(child, Tool(child + ":read", "Read build output", "in_progress"));
		await HoldOnceAsync("activity-subagent", ct).ConfigureAwait(false);
		UpdateOn(child, new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = child + ":read",
			["status"] = "completed",
			["content"] = new JsonArray(Content(Text("Build output: 12 checks passed."))),
		});
		UpdateOn(child, MessageUpdate("Reviewed the build output."));
		FinishSubagent(_sessionId!, child, "completed");
		Message("Ready for review.");
		Update(Tool("trailing-check", "Trailing verification", "failed"));
		return new JsonObject { ["stopReason"] = "end_turn" };
	}
}
