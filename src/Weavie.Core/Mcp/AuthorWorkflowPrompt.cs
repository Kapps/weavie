namespace Weavie.Core.Mcp;

/// <summary>
/// The <c>author-workflow</c> prompt: the judgment for writing a Weavie workflow that the schema can't carry. The
/// field list stays in <c>getWorkflowSchema</c>, so the two never drift. See docs/specs/workflows.md.
/// </summary>
public static class AuthorWorkflowPrompt {
	/// <summary>The registered prompt (name, description, text).</summary>
	public static McpPrompt Prompt { get; } = new() {
		Name = "author-workflow",
		Description = "Create, fork, change, or improve a Weavie workflow with the user.",
		Text = """
			Help the user create, fork, change, or improve a Weavie workflow: a YAML file of ordered stages that
			Weavie runs in a session. Weavie owns the run (which stage is current, approvals, decisions, typed
			outputs); the agent does each stage's work.

			1. Call `listWorkflows` to see what exists and `getWorkflowSchema` for the exact fields. Never write a
			   field the schema doesn't list.
			2. Interview the user about how they actually work, and model that process. Built-in workflows are
			   references for the format, not templates; fork one (copy it under the same id into a more
			   specific scope) only when it already matches what the user described.
			3. Write a personal workflow (`~/.weavie/workflows/<id>.yaml`) unless the user says the process
			   belongs to this repository (`.weavie/workflows/<id>.yaml`). The `id` must equal the file name.
			4. Prefer the user's existing skills as stage steps (`skill` + `args`) over restating them as prompts.
			5. Use `gate: approve` where the user wants to sign off before the run moves on, and put `max` on
			   every decision that can loop back.
			6. Never ask a model about something the run already knows, such as whether a stage was skipped or
			   an output exists; use `else` and stage order for those. Reserve decisions (`next` with choices,
			   `when`) for genuine judgment, and phrase them so a small model can answer from the stage's
			   outputs alone.
			7. Declare `outputs` for anything a later stage needs, and reference it as `{{<stage>.<output>}}`;
			   reference run inputs as `{{inputs.<name>}}`.
			8. Call `validateWorkflow` on the file and fix every problem before finishing. Then summarize the
			   workflow's stages for the user and say where the file lives.
			""",
	};
}
