using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;

namespace Weavie.Core.Mcp;

// pinFinding: one thing the agent learned, on the board above the composer. See docs/specs/findings-board.md.
public sealed partial class McpServer {
	private const string PinFindingTool = AgentBlockRules.PinTool;

	private static readonly string FindingToolEntry = new JsonObject {
		["name"] = PinFindingTool,
		["description"] = "Pin a finding to the board Weavie shows beside the conversation. "
			+ "The user watches the board while you work, so a finding pinned when you learn it is read during the wait and not at the end. "
			+ "A finding is one thing you now know. Its title states it in one sentence and is all the user sees until they open it; the detail holds the evidence behind it. "
			+ "Pinning the same id again replaces that finding, which is how you correct one that turned out wrong. "
			+ "The board stays on screen beside your reply, and a finding also appears in your reply where you write its id as [[id]] on a line of its own; don't repeat a finding's content in your reply.",
		["inputSchema"] = new JsonObject {
			["type"] = "object",
			["properties"] = new JsonObject {
				["id"] = new JsonObject { ["type"] = "string", ["description"] = AgentBlockRules.IdRule + "." },
				["title"] = new JsonObject { ["type"] = "string", ["description"] = "One sentence that states the finding. Always visible." },
				["detail"] = new JsonObject { ["type"] = "string", ["description"] = "Markdown. The evidence behind it; closed until the user opens it." },
			},
			["required"] = new JsonArray("id", "title"),
		},
	}.ToJsonString();

	private static StagedBlock StagePinFinding(JsonElement args, InflightCall call) =>
		StageBlock(args, call, PinFindingTool, AgentBlockJson.Default.AgentFindingRequest, request => {
			var current = call.Blocks.Find(request.Id);
			return (AgentBlockRules.Pin(request, current),
				$"Finding '{request.Id}' is {(current is null ? "pinned" : "replaced")} on the board. The user has it in front of them now. "
				+ $"In your reply, write [[{request.Id}]] on a line of its own where it belongs, and don't restate what it says.");
		});
}
