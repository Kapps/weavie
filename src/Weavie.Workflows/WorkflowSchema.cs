using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.Workflows;

/// <summary>The one field table for workflow files: validation and the published JSON Schema both derive from it.</summary>
public static partial class WorkflowSchema {
	internal const string Done = "done";

	internal static IReadOnlyList<string> ValueTypes { get; } = ["text", "url", "list", "files", "pr"];

	private static RefShape StageRef { get; } = new("stage");

	internal static WorkflowShape Root { get; } = Build();

	/// <summary>The JSON Schema of a workflow file.</summary>
	public static string Json { get; } = BuildJson();

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	internal static partial Regex IdPattern();

	/// <summary>Whether <paramref name="text"/> is a name a step's <c>role</c> can ask for.</summary>
	public static bool IsRoleName(string text) => IdPattern().IsMatch(text);

	[GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
	private static partial Regex NamePattern();

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:_.-]*$")]
	private static partial Regex NativeIdPattern();

	private static WorkflowShape Build() {
		const string idHint = "lowercase words joined by hyphens";
		const string nameHint = "a letter followed by letters or digits";
		string[] inputTypes = [.. ValueTypes, .. ValueTypes.Select(t => t + "?")];
		var outputs = new MapShape(
			"The stage's values by name. A stage with run declares each one's type (text, url, list, files, or pr) and its agent "
			+ "reports it; files must be worktree-relative or absolute within {{run.documents}}. A stage with stages copies each "
			+ "one from a stage inside it: \"{{stage.output}}\". A repeated stage's output holds one value per entry.",
			NamePattern(), nameHint, new TextShape("A type, or one {{stage.output}} reference."));
		var work = new ObjectShape("The work: a prompt, or a skill run as its real slash command.", [
			new("prompt", new TextShape("Instruction text sent as the stage's turn; references: {{run.prompt}} (what the run was started with), {{inputs.name}}, {{stage.output}}, {{stage.item}} and {{stage.index}} inside a repeated stage, {{run.documents}} (shared documents directory)."), false),
			new("skill", new TextShape(
				"A skill or slash command the agent already has, without the leading slash.",
				NativeIdPattern(), "a skill name without the leading slash"), false),
			new("args", new TextShape("Text passed after the skill name; may use {{…}} references."), false),
		]);
		var context = new TextOrObjectShape(
			"The conversation the stage runs in, relative to the one it sits in (main at the top). continue (default) runs in that "
			+ "conversation; fork copies it on every visit; fresh opens one conversation on the stage's first visit and continues it on "
			+ "every return; { reuse: <stage> } continues a fork or fresh sibling's conversation. A stage with stages runs them in its "
			+ "conversation. All share the worktree.",
			new EnumShape("continue, fork, or fresh.", ["continue", "fork", "fresh"]),
			new ObjectShape("Continue a sibling's conversation.", [
				new("reuse", new TextShape("The id of a fork or fresh sibling whose conversation this stage continues, on its agent.", IdPattern(), idHint), true),
			]));
		var agent = new ObjectShape("An agent steps refer to by name; every id is the provider's own, never inferred.", [
			new("provider", new TextShape(
				"The provider id, e.g. claude-acp; a different provider requires context: fresh."), true),
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
			new TextShape("A sibling's id, or done."),
			new ObjectShape("A described answer.", [
				new("then", new TextShape("A sibling's id, or done."), true),
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
				"Each answer and the sibling (or done) it leads to; an answer leading back to this stage reopens its work with the user's feedback, or enters a stage with stages again.",
				IdPattern(), idHint, choice), true),
			new("limit", new IntShape(
				"How many times in a row the model may answer this decision before the user is asked instead; model decisions only.", 1), false),
			new("by", by, false),
			confidence,
		]);
		var stage = new ObjectShape("One stage: work it runs (run) or stages it holds (stages), exactly one of the two.", [
			new("id", new TextShape("Unique within the workflow file; run is reserved for built-in references.", IdPattern(), idHint), true),
			new("if", condition, false),
			new("foreach", new TextShape(
				"One {{stage.output}} reference to a list or files value: the stage runs once per entry, in order, each entry with its "
				+ "own conversations. Inside, {{<this stage>.item}} is the entry and {{<this stage>.index}} its number from 1."), false),
			new("agent", new TextShape(
				"The name of an agents entry that runs this stage's conversation; omit agent and role to run on the session's own model, "
				+ "or a fork's source model.", IdPattern(), idHint), false),
			new("role", new TextShape(
				"A role the user fills with a model for the session's provider (planner, builder, reviewer by convention); a stage "
				+ "takes agent or role, not both.", IdPattern(), idHint), false),
			new("context", context, false),
			new("run", work, false),
			new("stages", new ListShape("The stages it holds, run in order unless a stage's then says otherwise.", StageRef), false),
			new("outputs", outputs, false),
			new("then", new TextOrObjectShape(
				"What follows: a sibling's id, done (finishes the list this stage is in), or a decision. Without it the run moves to the "
				+ "next sibling.",
				new TextShape("A sibling's id, or done."), decision), false),
		]);
		StageRef.Target = stage;
		return new ObjectShape("A workflow: ordered stages the host runs and the agent fills. Evidence warnings are flags for judgment; "
			+ "authors may ignore intentional warnings. Warnings never block saving or running; structural errors remain invalid.", [
			new("id", new TextShape("The workflow id; must match the file name.", IdPattern(), idHint), true),
			new("description", new TextShape("One line, shown when choosing a workflow and read as its criteria when one is suggested for a prompt."), true),
			new("inputs", new MapShape(
				"Values an agent starts the run with, by name; a ? suffix on the type marks one optional. Declare none in a workflow the user starts from a prompt.",
				NamePattern(), nameHint, new EnumShape("The value's type.", inputTypes)), false),
			new("agents", new MapShape("Agents steps refer to by name.", IdPattern(), idHint, agent), false),
			new("stages", new ListShape("The stages, run in order unless a stage's then says otherwise.", StageRef), true),
		]);
	}

	private static string BuildJson() {
		var schema = Root.Schema();
		schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
		schema["title"] = "Workflow";
		schema["$defs"] = new JsonObject { ["stage"] = StageRef.Target!.Schema() };
		return schema.ToJsonString();
	}
}
