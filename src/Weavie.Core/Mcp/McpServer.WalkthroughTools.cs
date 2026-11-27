using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using static Weavie.Core.Mcp.McpSchema;

namespace Weavie.Core.Mcp;

// publishWalkthrough / updateWalkthrough: a guided read of code, drawn in an editor tab from the pane's card. See
// docs/specs/code-walkthroughs/design.md. The agent sends anchors, never code; Weavie reads and checks every line.
public sealed partial class McpServer {
	private const string PublishWalkthroughTool = AgentWalkthroughRules.PublishTool;
	private const string UpdateWalkthroughTool = AgentBlockRules.UpdateWalkthroughTool;

	private static readonly string WalkthroughToolEntries = WalkthroughSchemas();

	private static string WalkthroughSchemas() {
		JsonObject Limited(int limit) => new() { ["type"] = "string", ["maxLength"] = limit };
		JsonObject Code() => Object(new JsonObject {
			["path"] = Described(Text(), "The file, relative to the worktree."),
			["from"] = Described(Integer(), "The first line shown. Start at the function's or type's first line."),
			["to"] = Described(Integer(), "The last line shown. End at its last line."),
			["folds"] = Described(Array(Object(new JsonObject {
				["from"] = Integer(),
				["to"] = Integer(),
				["label"] = Described(Limited(AgentWalkthroughLimits.Title), "What the folded lines hold, e.g. 'turn bookkeeping: is a tool still running'."),
			}, "from", "to", "label")), "Line ranges that are not the point, hidden behind their label until the reader opens them."),
			["notes"] = Described(Array(Object(new JsonObject {
				["line"] = Integer(),
				["token"] = Described(Text(), "Text on that line the note is about; Weavie marks it. It must appear on the line exactly."),
				["text"] = Described(Limited(AgentWalkthroughLimits.Title), "One plain sentence the reader would not get from the code alone."),
			}, "line", "token", "text")), "Notes drawn under their lines. A note sits on a line outside every fold."),
		}, "path", "from", "to");
		JsonObject Stop(bool update) {
			var properties = new JsonObject {
				["id"] = Described(Text(), AgentBlockRules.IdRule + "; unique in the walkthrough."),
				["name"] = Described(Limited(AgentWalkthroughLimits.Name), "A short label for the strip of stops, e.g. 'Waiting room'."),
				["title"] = Described(Limited(AgentWalkthroughLimits.Title), "The stop's heading."),
				["lead"] = Described(Text(), "Markdown: what to read this stop's code for, in two or three sentences."),
				["code"] = Described(Array(Code()), "The code of this step: whole functions or types."),
				["state"] = Described(Object(new JsonObject {
					["cells"] = Array(Object(new JsonObject { ["label"] = Text(), ["value"] = Described(Text(), "Empty reads as nothing.") }, "label", "value")),
					["note"] = Text(),
				}, "cells"), "The followed example after this step, e.g. what is held, saved and on screen. Your account; Weavie can't check it."),
				["why"] = Described(Object(new JsonObject {
					["title"] = Described(Text(), "The question it answers, e.g. 'Why not draw the chart here?'"),
					["text"] = Described(Text(), "Markdown."),
					["source"] = Object(new JsonObject {
						["kind"] = Described(Enum(AgentWalkthroughRules.SourceKinds), "reading: your own reading of the code, when no spec, PR or commit says why."),
						["ref"] = Described(Text(), "The spec path, PR or commit; absent for a reading."),
					}, "kind"),
				}, "title", "text", "source"), "Why the code is built this way, where this stop's code shows it."),
				["predict"] = Described(Object(new JsonObject { ["question"] = Text(), ["answer"] = Text() }, "question", "answer"),
					"A question the reader answers before reading on; the answer stays hidden until asked for."),
			};
			if (!update) return Object(properties, "id", "name", "title", "lead", "code");
			properties["after"] = Described(Text(), "For an added stop: the stop it follows. Without it the stop goes last.");
			properties["effect"] = Described(Enum(AgentWalkthroughRules.Effects),
				"For a stop that exists: changed when its explanation changed; affected when its code is untouched but plays out differently now.");
			properties["note"] = Described(Limited(AgentWalkthroughLimits.Title), "What changed about the stop, e.g. 'Was: … Now: …'. Required for a stop that exists.");
			properties["code"] = Described(Array(Code()), "Every block of the stop, recorded again against the edited files. It replaces the stop's code.");
			return Object(properties, "id");
		}
		JsonObject Walkthrough(bool update) {
			var properties = new JsonObject {
				["id"] = Described(Text(), AgentBlockRules.IdRule + "; unique in this conversation."),
				["title"] = Described(Limited(AgentWalkthroughLimits.Title), "e.g. 'How a chart the agent asks for lands on its own row'."),
				["problem"] = Described(Text(), "Markdown: what the code has to solve, and the question that raises."),
				["gist"] = Described(Text(), "Markdown: the answer in one paragraph, before any code."),
				["example"] = Described(Text(), "The one concrete case followed through every stop, e.g. a call with its real arguments."),
				["closing"] = Described(Text(), "Markdown after the last stop, e.g. a mermaid diagram of the whole path."),
				["stops"] = Described(Array(Stop(update)), update
					? "One entry per stop the change alters or adds. A stop that exists takes effect and note, plus the fields that changed."
					: "The steps, in the order the code runs."),
			};
			if (!update) return Object(properties, "id", "title", "problem", "gist", "stops");
			properties["change"] = Described(Text(), "One or two sentences on what you changed.");
			return Object(properties, "id", "change");
		}
		string limits = $"Limits: {AgentWalkthroughLimits.Stops} stops, {AgentWalkthroughLimits.Blocks} code blocks per stop, "
			+ $"{AgentWalkthroughLimits.Lines} lines, {AgentWalkthroughLimits.Folds} folds and {AgentWalkthroughLimits.Notes} notes per block.";
		var publish = Tool(PublishWalkthroughTool,
			"Publish a walkthrough: a guided read of how some code works, for someone who has not seen it, drawn by Weavie in an editor tab. "
			+ "Open with the problem the code solves and the question it raises, then answer it in one paragraph (gist). Then go through the code in "
			+ "the order it runs, one stop per step. Show whole functions or types, never excerpts; fold what is not the point and say in the fold's "
			+ "label what it holds. Put a note under each line that matters, in plain words, naming the token it is about. Follow one concrete example "
			+ "through every stop and give its state after each. Say why the code is built this way where the code shows it, citing a spec, PR or commit, "
			+ "or marking it as your own reading. A diagram is a mermaid block in any markdown field, where a picture explains something. Send anchors "
			+ "(path, lines, tokens), never code: Weavie reads the lines, checks every anchor, and refuses a wrong one with the real text. " + limits,
			Walkthrough(update: false));
		var update = Tool(UpdateWalkthroughTool,
			"Update a walkthrough you published, after your edits change the code it explains. Say what you changed. Change every stop the change "
			+ "alters, including stops whose code you did not touch, recording their code again against the edited files, and add stops for what you "
			+ "found while editing. Stops you don't name stay as they are. " + limits,
			Walkthrough(update: true));
		return publish.ToJsonString() + "," + update.ToJsonString();
	}

	private Task HandleWalkthroughToolAsync(IMcpResponder responder, JsonElement args, string? idRaw, InflightCall call, bool publish, CancellationToken ct) =>
		StageBlockAsync(responder, args, idRaw, call, publish ? PublishWalkthroughTool : UpdateWalkthroughTool, json => {
			var request = JsonSerializer.Deserialize(json, AgentBlockJson.Default.AgentWalkthroughRequest)
				?? throw new AgentBlockException("The arguments must be an object.");
			var current = call.Blocks.Find(request.Id);
			var reads = (_walkthroughs ?? throw new ToolUnavailableException("Walkthroughs are shown only in Weavie's agent pane.")).Begin();
			return publish
				? (AgentWalkthroughRules.Publish(request, current, reads),
					$" Walkthrough '{request.Id}' is shown to the user. After you change the code it explains, call {UpdateWalkthroughTool}: change every stop "
					+ "the change alters, including stops whose code you did not touch, and add stops for what you found while editing.")
				: (AgentWalkthroughRules.Update(request, current, reads),
					$" Walkthrough '{request.Id}' is updated; the user sees which stops changed.");
		}, ct);
}
