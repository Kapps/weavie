using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Weavie.Core.Workflows;

/// <summary>The one field table for workflow files: validation and the published JSON Schema both derive from it.</summary>
internal static partial class WorkflowSchema {
	public const string Done = "done";

	public static IReadOnlyList<string> ValueTypes { get; } = ["text", "url", "list", "files", "pr"];

	public static WorkflowShape Root { get; } = Build();

	public static string Json { get; } = BuildJson();

	[GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
	public static partial Regex IdPattern();

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
		var choose = new ObjectShape("A decision whose answer picks the next stage; an unsure answer pauses the run.", [
			new("prompt", new TextShape("The question; may use {{…}} references."), true),
			new("choices", new MapShape(
				"Each answer and the stage id (or done) it leads to.",
				IdPattern(), idHint, new TextShape("A stage id, or done.")), true),
			new("max", new IntShape("How often each transition from this stage may be taken before the run pauses.", 1), false),
		]);
		var stage = new ObjectShape("One stage of the run.", [
			new("id", new TextShape("Unique within the workflow.", IdPattern(), idHint), true),
			new("when", new TextShape("A yes/no question; a no skips the stage. May use {{…}} references."), false),
			new("else", new TextShape("Stage id (or done) to jump to when `when` skips this stage."), false),
			new("do", work, true),
			new("gate", new EnumShape("The user approves (or rejects with feedback) before the run advances.", ["approve"]), false),
			new("next", new TextOrObjectShape(
				"What follows: a stage id, done, or a decision. Stages otherwise advance in order.",
				new TextShape("A stage id, or done."), choose), false),
		]);
		return new ObjectShape("A Weavie workflow: ordered stages that Weavie runs and the agent fills.", [
			new("id", new TextShape("The workflow id; must match the file name.", IdPattern(), idHint), true),
			new("description", new TextShape("One line, shown when choosing a workflow."), true),
			new("inputs", new MapShape(
				"Values the run starts with, by name; a ? suffix on the type marks one optional.",
				NamePattern(), nameHint, new EnumShape("The value's type.", inputTypes)), false),
			new("stages", new ListShape("The stages, run in order unless next/else says otherwise.", stage), true),
		]);
	}

	private static string BuildJson() {
		var schema = Root.Schema();
		schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
		schema["title"] = "Weavie workflow";
		return schema.ToJsonString();
	}
}
