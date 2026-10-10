using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// Subagents in the adapters' wire shapes; holds are released by files the test writes.
internal sealed partial class FakeAcpAgent {
	private int _subagentSequence;

	private async Task<JsonNode> SubagentAsync(string text, CancellationToken ct) {
		string child = NextSubagent();
		switch (text) {
			case "subagent":
				var spawn = Tool("spawn-agent", "Spawn Explore", "in_progress");
				spawn["_meta"] = new JsonObject { ["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["version"] = 1, ["subagent"] = new JsonObject() } } };
				Update(spawn);
				SpawnSubagent(_sessionId!, child, "Explore", "Count files");
				SubagentWork(child, "Counted 3 files");
				FinishSubagent(_sessionId!, child, "completed");
				Update(new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "spawn-agent", ["status"] = "completed" });
				// Claude announces its Agent tool only on the child, yet sends the parent its metadata.
				Update(new JsonObject {
					["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "toolu_hidden_agent",
					["_meta"] = new JsonObject { ["claudeCode"] = new JsonObject { ["toolName"] = "Agent" } },
				});
				break;
			case "subagent-held":
				SpawnSubagent(_sessionId!, child, "Explore", "Count files");
				UpdateOn(child, MessageUpdate("counting"));
				File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "live-subagent"), child);
				_ = Task.Run(async () => {
					await WaitForFileAsync("release-subagent", ct).ConfigureAwait(false);
					SubagentWork(child, "held subagent finished");
					FinishSubagent(_sessionId!, child, "completed");
				}, ct);
				break;
			case "subagent-nested":
				string nested = NextSubagent();
				SpawnSubagent(_sessionId!, child, "Planner", "Plan the change");
				SpawnSubagent(child, nested, "Reader", "Read the files");
				SubagentWork(nested, "nested finished");
				FinishSubagent(child, nested, "completed");
				FinishSubagent(_sessionId!, child, "completed");
				break;
			case "subagent-permission":
				SpawnSubagent(_sessionId!, child, "Editor", "Edit with approval");
				var result = await Connection().RequestAsync("session/request_permission", new JsonObject {
					["sessionId"] = child,
					["toolCall"] = new JsonObject { ["toolCallId"] = child + ":edit", ["title"] = "Edit README", ["kind"] = "edit" },
					["options"] = new JsonArray(
						new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
						new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" }),
				}, ct).ConfigureAwait(false);
				UpdateOn(child, MessageUpdate("child permission: " + result.GetProperty("outcome").GetProperty("optionId").GetString()));
				FinishSubagent(_sessionId!, child, "completed");
				break;
			case "subagent-duplicate":
				SpawnSubagent(_sessionId!, child, "Explore", "Count files");
				SpawnSubagent(_sessionId!, child, "Explore", "Count files again");
				break;
			default:
				throw AcpAdapterException.InvalidParams($"Unknown fake subagent mode '{text}'.");
		}
		Message("subagent turn done");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	// Claude replays a loaded session's subagents under distinct ids, nested ones on their replayed parent.
	private void ReplaySubagents(string sessionId) {
		if (File.Exists(Path.Combine(Environment.CurrentDirectory, "replay-subagents"))) {
			string replayed = sessionId + ":replay-subagent:toolu_1";
			string nested = sessionId + ":replay-subagent:toolu_2";
			SpawnSubagent(sessionId, replayed, "Explore", "Replayed task");
			SubagentWork(replayed, "replayed child output");
			SpawnSubagent(replayed, nested, "Reader", "Replayed nested task");
			UpdateOn(nested, MessageUpdate("replayed nested output"));
			FinishSubagent(replayed, nested, "disconnected");
			FinishSubagent(sessionId, replayed, "disconnected");
		}
		// Codex's fork load announces the live child under its live id, then disconnects it on the fork.
		string live = Path.Combine(Environment.CurrentDirectory, "live-subagent");
		if (sessionId.StartsWith("fake-fork-", StringComparison.Ordinal) && File.Exists(live)
			&& File.Exists(Path.Combine(Environment.CurrentDirectory, "fork-announces-live-subagent"))) {
			string child = File.ReadAllText(live);
			SpawnSubagent(sessionId, child, "Explore", "Count files");
			FinishSubagent(sessionId, child, "disconnected");
		}
	}

	// A test opts into a subagent announced on a session after Weavie closed it.
	private void SpawnAfterClose() {
		if (!File.Exists(Path.Combine(Environment.CurrentDirectory, "spawn-after-close"))) return;
		string child = NextSubagent();
		SpawnSubagent(_sessionId!, child, "Explore", "Late task");
		SubagentWork(child, "late child output");
		FinishSubagent(_sessionId!, child, "completed");
	}

	private string NextSubagent() => $"{_sessionId}/subagent-{++_subagentSequence}";

	private void SpawnSubagent(string parent, string child, string name, string task) => UpdateOn(parent, new JsonObject {
		["sessionUpdate"] = "subagent_spawned",
		["subagentSessionId"] = child,
		["name"] = name,
		["task"] = task,
		["capabilities"] = new JsonObject(),
	});

	private void SubagentWork(string child, string reply) {
		UpdateOn(child, Tool(child + ":tool", "Count files", "in_progress"));
		UpdateOn(child, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = child + ":tool", ["status"] = "completed" });
		UpdateOn(child, MessageUpdate(reply));
	}

	private void FinishSubagent(string parent, string child, string state) => UpdateOn(parent, new JsonObject {
		["sessionUpdate"] = "subagent_state_update",
		["subagentSessionId"] = child,
		["state"] = state,
	});

	private static JsonObject Tool(string id, string title, string status) => new() {
		["sessionUpdate"] = "tool_call", ["toolCallId"] = id, ["title"] = title, ["kind"] = "search", ["status"] = status,
	};

	private static JsonObject MessageUpdate(string text) => new() {
		["sessionUpdate"] = "agent_message_chunk", ["messageId"] = Guid.NewGuid().ToString("N"), ["content"] = Text(text),
	};

	private void UpdateOn(string sessionId, JsonObject update) =>
		Connection().Notify("session/update", new JsonObject { ["sessionId"] = sessionId, ["update"] = update });

	private static async Task WaitForFileAsync(string name, CancellationToken ct) {
		string path = Path.Combine(Environment.CurrentDirectory, name);
		while (!File.Exists(path)) await Task.Delay(10, ct).ConfigureAwait(false);
	}
}
