using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.Workflows;

/// <summary>The one field table for workflow files: validation and the published JSON Schema both derive from it.</summary>
public static partial class WorkflowSchema {
	internal const string Done = "done";

	internal static IReadOnlyList<string> ValueTypes { get; } = ["text", "url", "list", "files", "pr"];

	internal static WorkflowShape Root { get; } = Build();

	/// <summary>The JSON Schema of a workflow file.</summary>
	public static string Json { get; } = BuildJson();

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	internal static partial Regex IdPattern();

	[GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
	private static partial Regex NamePattern();

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:_.-]*$")]
	private static partial Regex NativeIdPattern();

	private static WorkflowShape Build() {
		const string idHint = "lowercase words joined by hyphens";
		const string nameHint = "a letter followed by letters or digits";
		string[] inputTypes = [.. ValueTypes, .. ValueTypes.Select(t => t + "?")];
		var outputs = new MapShape(
			"Typed values the step must report when it completes, by name. Files must be worktree-relative or absolute within {{run.documents}}.",
			NamePattern(), nameHint, new EnumShape("The value's type.", ValueTypes));
		var work = new ObjectShape("The work step: a prompt, or a skill run as its real slash command.", [
			new("prompt", new TextShape("Instruction text sent as the stage's turn; references: {{inputs.name}}, {{stage.output}}, {{run.documents}} (shared documents directory)."), false),
			new("skill", new TextShape(
				"A skill or slash command the agent already has, without the leading slash.",
				NativeIdPattern(), "a skill name without the leading slash"), false),
			new("args", new TextShape("Text passed after the skill name; may use {{…}} references."), false),
			new("outputs", outputs, false),
			new("agent", new TextShape(
				"The name of an agents entry that runs this step; omit to run on the session's own model.", IdPattern(), idHint), false),
		]);
		var agent = new ObjectShape("An agent steps refer to by name; every id is the provider's own, never inferred.", [
			new("provider", new TextShape(
				"The provider id, e.g. claude-acp; a step using the agent runs only in that provider's sessions."), true),
			new("model", new TextShape("The provider's model id; omit for the session's model as it stands."), false),
			new("options", new MapShape(
				"The provider's config option values by option id, applied after the model.",
				NativeIdPattern(), "a provider config option id", new TextShape("The option's value id.")), false),
		]);
		var ask = new TextShape(
			"Ask about observable criteria, one concern per question. A model has no tools or conversation history: it sees "
			+ "this text with referenced values, the current text of explicitly referenced files inputs/outputs, stage output values, "
			+ "and the completion summary with user feedback. Literal/composed paths, unreferenced files, URLs and document links "
			+ "supply no contents. Any unreadable referenced file asks the user without calling the model.");
		var by = new EnumShape("Who answers: model (default; a low-confidence, refused, or failed answer goes to the user) or user.", ["model", "user"]);
		var confidence = new WorkflowField("confidence", new NumberShape("Minimum confidence to route automatically; model decisions only. Omit to use workflow.decisionConfidence.", 0, 1), false);
		var choice = new TextOrObjectShape("A destination with an optional description of this answer.",
			new TextShape("A stage id, or done."),
			new ObjectShape("A described answer.", [
				new("then", new TextShape("A stage id, or done."), true),
				new("description", new TextShape("Plain-text criteria distinguishing this answer; recommended for model decisions."), false),
			]));
		var condition = new TextOrObjectShape(
			"Run the stage only if the answer is yes; on no it is skipped and the run moves to the following stage. "
			+ "Text is a yes/no question for the model.",
			ask,
			new ObjectShape("A yes/no question.", [new("ask", ask, true), new("by", by, false), confidence]));
		var decision = new ObjectShape("A question whose answer picks the next stage.", [
			new("ask", ask, true),
			new("choices", new MapShape(
				"Each answer and the stage id (or done) it leads to; an answer leading back to this stage reopens it with the user's feedback.",
				IdPattern(), idHint, choice), true),
			new("limit", new IntShape(
				"How many times in a row the model may answer this decision before the user is asked instead; model decisions only.", 1), false),
			new("by", by, false),
			confidence,
		]);
		var stage = new ObjectShape("One stage of the run.", [
			new("id", new TextShape("Unique within the workflow; run is reserved for built-in references.", IdPattern(), idHint), true),
			new("if", condition, false),
			new("run", work, true),
			new("then", new TextOrObjectShape(
				"What follows: a stage id, done, or a decision. Without it the run moves to the following stage.",
				new TextShape("A stage id, or done."), decision), false),
		]);
		return new ObjectShape("A workflow: ordered stages the host runs and the agent fills. Evidence warnings are flags for judgment; "
			+ "authors may ignore intentional warnings. Warnings never block saving or running; structural errors remain invalid.", [
			new("id", new TextShape("The workflow id; must match the file name.", IdPattern(), idHint), true),
			new("description", new TextShape("One line, shown when choosing a workflow."), true),
			new("inputs", new MapShape(
				"Values the run starts with, by name; a ? suffix on the type marks one optional.",
				NamePattern(), nameHint, new EnumShape("The value's type.", inputTypes)), false),
			new("agents", new MapShape("Agents steps refer to by name.", IdPattern(), idHint, agent), false),
			new("stages", new ListShape("The stages, run in order unless a stage's then says otherwise.", stage), true),
		]);
	}

	private static string BuildJson() {
		var schema = Root.Schema();
		schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
		schema["title"] = "Workflow";
		return schema.ToJsonString();
	}
}
