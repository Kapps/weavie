using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private async Task QuestionAsync(CancellationToken ct) {
		var choices = new JsonArray();
		foreach (string label in new[] {
			"Latest reasoning summary as one compact status row (recommended)",
			"All thinking since my latest message, as in mockup C",
		}) {
			choices.Add(new JsonObject { ["const"] = label, ["title"] = label });
		}
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = "Codex keeps working while you answer. An answer sent after it finishes is not delivered.",
			["requestedSchema"] = new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["question_0"] = new JsonObject {
						["type"] = "string",
						["title"] = "Given that distinction, what should “Current” show? Actual message replies will stay visible in either case.",
						["oneOf"] = choices,
					},
					["question_0_note"] = new JsonObject {
						["type"] = "string",
						["title"] = "Additional answer or note",
						["_meta"] = Air("customAnswer", true),
					},
				},
			},
		}, ct).ConfigureAwait(false);
		Message("question response: " + JsonSerializer.Serialize(result));
	}
}
