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
			Weavie runs in a session. Weavie owns the run (which stage is current, decisions, typed outputs); the
			agent does each stage's work.

			1. Call `listWorkflows` to see what exists and `getWorkflowSchema` for the exact fields. Never write a
			   field the schema doesn't list.
			2. Interview the user about how they actually work, and model that process. Built-in workflows are
			   references for the format, not templates; fork one (copy it under the same id into a more
			   specific scope) only when it already matches what the user described.
			3. Write a personal workflow (`~/.weavie/workflows/<id>.yaml`) unless the user says the process
			   belongs to this repository (`.weavie/workflows/<id>.yaml`). The `id` must equal the file name.
			4. Prefer the user's existing skills as stage steps (`skill` + `args`) over restating them as prompts.
			5. Route with `then`: a stage id or `done` to go there, or a decision (`ask` + `choices`, each answer
			   naming the stage it leads to). Stages without `then` move to the following stage. Use `if` to run
			   a stage only when a yes/no question says so; a skipped stage falls through to the following one.
			6. Approvals are decisions the user answers: `by: user`, with choices such as `approve` → the next
			   stage and `revise` → this stage (an answer leading back to the stage reopens it with the user's
			   feedback). Put one wherever the user wants to sign off.
			7. A decision answered by the model (`by: model`, the default) runs on a model with no tools — it
			   may be a small decision model. It sees only its `ask` text with the `{{…}}` values filled in, the
			   stage's outputs, and the summary the agent gives at `completeStage`. It cannot read files, run
			   commands, or see the conversation. So anything a decision depends on must be a stage output:
			   shape outputs for the decisions that read them (a `remaining: list` the decision checks, not
			   "read tasks.md"). When the model is unsure, Weavie asks the user instead.
			8. Put `limit` on every model decision that can loop back: how many times in a row the model may
			   answer it before the user is asked.
			9. Never ask a model about something the run already knows, such as whether a stage was skipped or
			   an output exists; stage order and fixed `then` targets handle those.
			10. Declare `outputs` for anything a later stage needs, and reference it as `{{<stage>.<output>}}`;
			   reference run inputs as `{{inputs.<name>}}`.
			11. Only put commands and flags in prompts that you've verified exist (check `--help`), and make sure
			   every file or value a stage uses is produced by an earlier stage and reachable where it's used.
			12. Before finishing, walk through a run stage by stage: what each stage receives, what it reports,
			   and what each decision can see. Then call `validateWorkflow` on the file, fix every error, and
			   heed its warnings. Summarize the workflow's stages for the user and say where the file lives.
			""",
	};
}
