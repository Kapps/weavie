using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	// Codex's shape: the field title is the question and the message is a notice about answering.
	private Task QuestionAsync(CancellationToken ct) => ChoiceQuestionAsync(
		"Codex keeps working while you answer. An answer sent after it finishes is not delivered.",
		"Given that distinction, what should “Current” show? Actual message replies will stay visible in either case.",
		[
			"Latest reasoning summary as one compact status row (recommended)",
			"All thinking since my latest message, as in mockup C",
		],
		ct);

	// Claude's single-question shape: the message is the whole question and the field title is a short header.
	private Task LongQuestionAsync(CancellationToken ct) => ChoiceQuestionAsync(
		string.Join('\n', [
			"I am about to publish this issue. Publish it?",
			"",
			"## What happened",
			"The card showed `## Expected` in the middle of a sentence.",
			"",
			"## Steps to reproduce",
			"1. Ask a question with several paragraphs.",
			"2. Look at the question card.",
			"",
			"See src/web/src/agent/agent-questions.css:1 for the styles.",
			"",
			"```",
			new string('x', 400),
			"```",
		]),
		"Publish?",
		["Publish", "Edit first"],
		ct);

	private async Task ChoiceQuestionAsync(string message, string title, string[] labels, CancellationToken ct) {
		var choices = new JsonArray();
		foreach (string label in labels) choices.Add(new JsonObject { ["const"] = label, ["title"] = label });
		var result = await Connection().RequestAsync("elicitation/create", new JsonObject {
			["sessionId"] = _sessionId,
			["mode"] = "form",
			["message"] = message,
			["requestedSchema"] = new JsonObject {
				["type"] = "object",
				["properties"] = new JsonObject {
					["question_0"] = new JsonObject { ["type"] = "string", ["title"] = title, ["oneOf"] = choices },
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
