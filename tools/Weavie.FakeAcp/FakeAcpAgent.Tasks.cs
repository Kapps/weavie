using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// Async tasks in the AIR wire shapes of Claude's and Codex's adapters.
internal sealed partial class FakeAcpAgent {
	private readonly ConcurrentDictionary<string, string> _tasks = new(StringComparer.Ordinal);

	private JsonObject TaskPrompt(string text, CancellationToken ct) {
		switch (text) {
			case "task-held":
				string tool = "exec-" + Guid.NewGuid().ToString("N");
				Update(Tool(tool, "sleep 30", "in_progress"));
				Update(new JsonObject {
					["sessionUpdate"] = "tool_call_update",
					["toolCallId"] = tool,
					["_meta"] = new JsonObject { ["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["asyncTasks"] = new JsonObject { ["backgrounded"] = true } } } },
				});
				SpawnTask(_sessionId!, tool, "sleep 30", "shell", tool);
				_ = ReleaseTaskAsync(_sessionId!, tool, "release-task", tool, ct);
				break;
			case "workflow-held":
				string workflow = "wf-" + Guid.NewGuid().ToString("N")[..8];
				SpawnTask(_sessionId!, workflow, "code-review", "workflow", null);
				UpdateOn(_sessionId!, new JsonObject {
					["sessionUpdate"] = "async_task_progress",
					["asyncTaskId"] = workflow,
					["description"] = "Review: correctness",
					["usage"] = new JsonObject { ["totalTokens"] = 1200, ["toolUses"] = 3, ["durationMs"] = 900 },
				});
				_ = ReleaseTaskAsync(_sessionId!, workflow, "release-workflow", null, ct);
				break;
			case "task-in-subagent":
				string child = NextSubagent();
				SpawnSubagent(_sessionId!, child, "Runner", "Run in background");
				SpawnTask(child, "child-task", "child sleep", "shell", null);
				break;
			default:
				throw AcpAdapterException.InvalidParams($"Unknown fake task mode '{text}'.");
		}
		Message("task turn done");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private void SpawnTask(string sessionId, string id, string name, string type, string? toolCallId) {
		_tasks[id] = sessionId;
		var update = new JsonObject {
			["sessionUpdate"] = "async_task_spawned",
			["asyncTaskId"] = id,
			["name"] = name,
			["taskType"] = type,
			["description"] = name + " description",
			["showInTranscript"] = false,
			["canStop"] = true,
		};
		if (toolCallId is not null) update["toolCallId"] = toolCallId;
		UpdateOn(sessionId, update);
	}

	private async Task ReleaseTaskAsync(string sessionId, string id, string release, string? tool, CancellationToken ct) {
		await WaitForFileAsync(release, ct).ConfigureAwait(false);
		if (!_tasks.TryRemove(id, out _)) return;
		TaskState(sessionId, id, "completed");
		if (tool is not null) UpdateOn(sessionId, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = tool, ["status"] = "completed" });
		else TaskState(sessionId, id, "completed");
	}

	// Claude acknowledges a stop with a notice; a test may then have it correct the task to completed.
	private JsonObject StopTask(JsonElement parameters) {
		string session = AcpJson.RequiredString(parameters, "sessionId", "async task stop");
		string id = AcpJson.RequiredString(parameters, "asyncTaskId", "async task stop");
		File.AppendAllText(StatePath("stops.log"), $"{session}:{id}{Environment.NewLine}");
		if (!_tasks.TryRemove(id, out string? owner)) return new JsonObject { ["stopped"] = false };
		TaskState(owner, id, "stopped");
		UpdateOn(session, new JsonObject { ["sessionUpdate"] = "notice", ["severity"] = "info", ["title"] = "Task stopped by user", ["description"] = id + "." });
		if (File.Exists(Path.Combine(Environment.CurrentDirectory, "correct-stop"))) TaskState(owner, id, "completed");
		return new JsonObject { ["stopped"] = true };
	}

	private void TaskState(string sessionId, string id, string state) => UpdateOn(sessionId, new JsonObject {
		["sessionUpdate"] = "async_task_state_update",
		["asyncTaskId"] = id,
		["state"] = state,
	});

	// Codex marks a choice question's note companion; Claude names the multi-select its custom field completes.
	private async Task CustomAnswerAsync(bool multiple, CancellationToken ct) {
		var choices = new JsonArray(
			new JsonObject { ["const"] = "Alpha", ["title"] = "Alpha" },
			new JsonObject { ["const"] = "Beta", ["title"] = "Beta" });
		var properties = multiple
			? new JsonObject {
				["question_0"] = new JsonObject { ["type"] = "array", ["title"] = "Pick", ["items"] = new JsonObject { ["anyOf"] = choices } },
				["question_0_custom"] = new JsonObject { ["type"] = "string", ["title"] = "Other", ["_meta"] = AirCustomAnswer(new JsonObject { ["questionId"] = "question_0", ["isCustomAnswer"] = true }) },
			}
			: new JsonObject {
				["choice"] = new JsonObject { ["type"] = "string", ["title"] = "Pick", ["oneOf"] = choices },
				["choice_note"] = new JsonObject { ["type"] = "string", ["title"] = "Additional answer or note", ["_meta"] = AirCustomAnswer(true) },
			};
		var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
		if (!multiple) schema["required"] = new JsonArray("choice");
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = "Pick one",
			["requestedSchema"] = schema,
		}, ct).ConfigureAwait(false);
		Message("custom answer: " + JsonSerializer.Serialize(result.GetProperty("content")));
	}

	private static JsonObject AirCustomAnswer(JsonNode marker) => new() {
		["jetbrains"] = new JsonObject { ["air"] = new JsonObject { ["version"] = 1, ["customAnswer"] = marker } },
	};
}
