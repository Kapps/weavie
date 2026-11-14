using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// Replies the agent stops on among progress notes, marked the way Claude's and Codex's adapters mark them.
internal sealed partial class FakeAcpAgent {
	// Claude ends each model cycle with a usage update naming who started it, also inside a prompt it holds open.
	private async Task<JsonNode> ClaudeRepliesAsync(CancellationToken ct) {
		Message("Reading the settings first.");
		Update(Tool("replies-read", "Read settings", "completed"));
		Message("Nothing is overridden, and the checks are running.");
		CycleEnd("human");
		await HoldOnceAsync("replies-wake", ct).ConfigureAwait(false);
		Update(Tool("replies-check", "Read check output", "completed"));
		Message("Checks pass; reading the review next.");
		Update(Tool("replies-review", "Read review", "completed"));
		Message("The review is clean.");
		CycleEnd("task-notification");
		// A steer that interrupts a running tool ends the cycle with no message after the tool.
		Message("Publishing the change.");
		Update(Tool("replies-publish", "Publish change", "completed"));
		CycleEnd("human");
		Message("Everything is published.");
		CycleEnd("human");
		_ = LateReplyAsync(ct);
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	// A finished background job wakes Claude after its prompt resolved, as a task of type "turn".
	private async Task LateReplyAsync(CancellationToken ct) {
		await WaitForFileAsync("release-replies-late", ct).ConfigureAwait(false);
		string turn = "autonomous-turn-" + Guid.NewGuid().ToString("N")[..8];
		SpawnTask(_sessionId!, turn, "Claude is working", "turn", null);
		Update(Tool("replies-late", "Read job output", "completed"));
		Message("The late job finished.");
		_tasks.TryRemove(turn, out _);
		TaskState(_sessionId!, turn, "completed");
		CycleEnd("task-notification");
	}

	private void CycleEnd(string origin) => Update(new JsonObject {
		["sessionUpdate"] = "usage_update",
		["used"] = 123,
		["size"] = 4096,
		["cost"] = new JsonObject { ["amount"] = 0.1, ["currency"] = "USD" },
		["_meta"] = new JsonObject { ["_claude/origin"] = new JsonObject { ["kind"] = origin } },
	});

	// Codex tags every message chunk; its adapter has only been seen to end a turn on the final answer.
	private JsonObject CodexReplies() {
		PhaseMessage("Running the checks.", "commentary");
		Update(Tool("phase-check", "Run checks", "completed"));
		PhaseMessage("The checks pass.", "final_answer");
		Update(Tool("phase-tidy", "Tidy up", "completed"));
		PhaseMessage("Tidying is done.", "commentary");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private void PhaseMessage(string text, string phase) {
		var update = MessageUpdate(text);
		update["_meta"] = Air("phase", phase);
		Update(update);
	}

	// The agent stops on the message nothing draws, after a note and its tool.
	private JsonObject NoReplyStop() {
		Message("Refreshing the chart.");
		Update(Tool("replies-refresh", "Refresh chart", "completed"));
		Message("[[weavie:no-reply]]");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	// A wake-up that the next prompt cuts short sends no cycle end.
	private JsonObject CutShortReply(CancellationToken ct) {
		_ = Task.Run(async () => {
			await WaitForFileAsync("release-replies-cut", ct).ConfigureAwait(false);
			Message("Still checking.");
		}, ct);
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private JsonObject SilentTurn() {
		Update(Tool("replies-silent", "Read output", "completed"));
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private static JsonObject Air(string key, JsonNode value) => new() {
		["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["version"] = 1, [key] = value } },
	};
}
