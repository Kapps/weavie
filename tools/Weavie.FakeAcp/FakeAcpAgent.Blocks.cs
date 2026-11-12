using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

// Weavie tool calls narrated as tool rows, in every way an adapter may report them, so the block claim is tested
// against relays rather than one provider's habits:
//   `block:<relay>:<tool>:<json>`                 one call
//   `block-parallel:<relay>:<tool>:<json>\n<json>` two calls whose results are reported in reverse order
//   `block-subagent:<tool>:<json>`                 one call narrated on a subagent's session
internal sealed partial class FakeAcpAgent {
	private int _blockCallSequence;

	private async Task<JsonNode> BlockCallAsync(string text, CancellationToken ct) {
		string[] parts = text.Split(':', 4);
		if (parts[0] == "block-subagent") {
			string child = NextSubagent();
			SpawnSubagent(_sessionId!, child, "Charter", "Chart the data");
			await NarratedCallAsync(child, "claude", parts[1], string.Join(':', parts[2..]), ct).ConfigureAwait(false);
			FinishSubagent(_sessionId!, child, "completed");
		} else if (parts[0] == "block-parallel") {
			string[] calls = parts[3].Split('\n');
			string[] ids = [.. calls.Select(_ => NextBlockCall())];
			for (int index = 0; index < calls.Length; index++) Start(_sessionId!, ids[index], parts[1], parts[2], calls[index]);
			var results = new List<(string Text, bool Failed)>();
			foreach (string call in calls) results.Add(await PostWeavieToolAsync(parts[2], call, ct).ConfigureAwait(false));
			for (int index = calls.Length - 1; index >= 0; index--) Report(_sessionId!, ids[index], parts[1], results[index]);
		} else {
			await NarratedCallAsync(_sessionId!, parts[1], parts[2], parts[3], ct).ConfigureAwait(false);
		}
		Message("block call done");
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private async Task NarratedCallAsync(string session, string relay, string tool, string arguments, CancellationToken ct) {
		string id = NextBlockCall();
		if (relay == "first") {
			var early = await PostWeavieToolAsync(tool, arguments, ct).ConfigureAwait(false);
			var row = ClaudeStart(id, tool);
			row["status"] = Status(early);
			row["rawOutput"] = new JsonArray(TextBlock(early.Text));
			UpdateOn(session, row);
			return;
		}
		if (relay != "none") Start(session, id, relay, tool, arguments);
		var result = await PostWeavieToolAsync(tool, arguments, ct).ConfigureAwait(false);
		Report(session, id, relay, result);
	}

	private void Start(string session, string id, string relay, string tool, string arguments) {
		if (relay == "codex") {
			UpdateOn(session, new JsonObject {
				["sessionUpdate"] = "tool_call",
				["toolCallId"] = id,
				["kind"] = "execute",
				["title"] = "mcp.weavie." + tool,
				["status"] = "in_progress",
				["rawInput"] = new JsonObject { ["server"] = "weavie", ["tool"] = tool, ["arguments"] = JsonNode.Parse(arguments) },
			});
			return;
		}
		UpdateOn(session, ClaudeStart(id, tool));
		UpdateOn(session, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["rawInput"] = JsonNode.Parse(arguments) });
	}

	private void Report(string session, string id, string relay, (string Text, bool Failed) result) {
		JsonObject Done() => new() { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["status"] = Status(result) };
		JsonObject With(string field, JsonNode value) {
			var update = Done();
			update[field] = value;
			return update;
		}
		JsonArray Content(string text) => new(new JsonObject { ["type"] = "content", ["content"] = TextBlock(text) });
		switch (relay) {
			case "none":
				break;
			case "codex":
				UpdateOn(session, With("rawOutput", CodexResult(result.Text)));
				break;
			case "escaped":
				UpdateOn(session, With("rawOutput", CodexResult(result.Text).ToJsonString()));
				break;
			case "content":
				UpdateOn(session, With("content", Content(result.Text)));
				break;
			case "fenced":
				UpdateOn(session, With("content", Content("```\n" + result.Text + "\n```")));
				break;
			case "truncated":
				UpdateOn(session, With("rawOutput", result.Text[..Math.Min(result.Text.Length, 60)] + "… (truncated)"));
				break;
			case "altered":
				UpdateOn(session, With("rawOutput", result.Text.ToUpperInvariant()));
				break;
			case "noresult":
				UpdateOn(session, Done());
				break;
			case "late":
				UpdateOn(session, Done());
				UpdateOn(session, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["rawOutput"] = new JsonArray(TextBlock(result.Text)) });
				break;
			default:
				var claude = With("rawOutput", new JsonArray(TextBlock(result.Text)));
				claude["content"] = Content(result.Text);
				UpdateOn(session, claude);
				if (relay == "repeat") UpdateOn(session, claude.DeepClone().AsObject());
				break;
		}
	}

	private static JsonObject ClaudeStart(string id, string tool) => new() {
		["sessionUpdate"] = "tool_call",
		["toolCallId"] = id,
		["title"] = "mcp__weavie__" + tool,
		["kind"] = "other",
		["status"] = "pending",
		["rawInput"] = new JsonObject(),
	};

	private static JsonObject CodexResult(string text) => new() {
		["result"] = new JsonObject { ["content"] = new JsonArray(TextBlock(text)), ["structuredContent"] = null, ["_meta"] = null },
		["error"] = null,
	};

	private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

	private static string Status((string Text, bool Failed) result) => result.Failed ? "failed" : "completed";

	private string NextBlockCall() => $"block-call-{++_blockCallSequence}";
}
