using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.Workflows;

/// <summary>The one field table for workflow files: validation and the published JSON Schema both derive from it.</summary>
public static partial class WorkflowSchema {
	internal const string Done = "done";

	internal static IReadOnlyList<string> ValueTypes { get; } = ["text", "url", "list", "files", "pr"];

	internal static WorkflowShape Root { get; } = Build();

	/// <summary>The JSON Schema of a workflow file, as <c>getWorkflowSchema</c> publishes it.</summary>
	public static string Json { get; } = BuildJson();

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	internal static partial Regex IdPattern();

	[GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
	private static partial Regex NamePattern();

	[GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9:_.-]*$")]
	private static partial Regex SkillPattern();

	private static WorkflowShape Build() {
		const string idHint = "lowercase words joined by hyphens";
		const string nameHint = "a letter followed by letters or digits";
		string[] inputTypes = [.. ValueTypes, .. ValueTypes.Select(t => t + "?")];
		var outputs = new MapShape(
			"Typed values the step must report through completeStage, by name.",
			NamePattern(), nameHint, new EnumShape("The value's type.", ValueTypes));
		var work = new ObjectShape("The work step: a prompt, or a skill run as its real slash command.", [
			new("prompt", new TextShape("Instruction text sent as the stage's turn; may use {{…}} references."), false),
			new("skill", new TextShape(
				"A skill or slash command the agent already has, without the leading slash.",
				SkillPattern(), "a skill name without the leading slash"), false),
			new("args", new TextShape("Text passed after the skill name; may use {{…}} references."), false),
			new("outputs", outputs, false),
		]);
		var ask = new TextShape(
			"The question. A model answers it with no tools: it sees only this text (with its {{…}} values filled in), "
			+ "the stage's outputs, and the summary the agent gave at completeStage, so reference every value it needs.");
		var by = new EnumShape("Who answers: model (default; an unsure or failed answer goes to the user) or user.", ["model", "user"]);
		var condition = new TextOrObjectShape(
			"Run the stage only if the answer is yes; on no it is skipped and the run moves to the following stage. "
			+ "Text is a yes/no question for the model.",
			new TextShape("A yes/no question for the model; may use {{…}} references."),
			new ObjectShape("A yes/no question.", [new("ask", ask, true), new("by", by, false)]));
		var decision = new ObjectShape("A question whose answer picks the next stage.", [
			new("ask", ask, true),
			new("choices", new MapShape(
				"Each answer and the stage id (or done) it leads to; an answer leading back to this stage reopens it with the user's feedback.",
				IdPattern(), idHint, new TextShape("A stage id, or done.")), true),
			new("limit", new IntShape(
				"How many times in a row the model may answer this decision before the user is asked instead; model decisions only.", 1), false),
			new("by", by, false),
		]);
		var stage = new ObjectShape("One stage of the run.", [
			new("id", new TextShape("Unique within the workflow.", IdPattern(), idHint), true),
			new("if", condition, false),
			new("run", work, true),
			new("then", new TextOrObjectShape(
				"What follows: a stage id, done, or a decision. Without it the run moves to the following stage.",
				new TextShape("A stage id, or done."), decision), false),
		]);
		return new ObjectShape("A Weavie workflow: ordered stages that Weavie runs and the agent fills.", [
			new("id", new TextShape("The workflow id; must match the file name.", IdPattern(), idHint), true),
			new("description", new TextShape("One line, shown when choosing a workflow."), true),
			new("inputs", new MapShape(
				"Values the run starts with, by name; a ? suffix on the type marks one optional.",
				NamePattern(), nameHint, new EnumShape("The value's type.", inputTypes)), false),
			new("stages", new ListShape("The stages, run in order unless a stage's then says otherwise.", stage), true),
		]);
	}

	private static string BuildJson() {
		var schema = Root.Schema();
		schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
		schema["title"] = "Weavie workflow";
		return schema.ToJsonString();
	}
}
