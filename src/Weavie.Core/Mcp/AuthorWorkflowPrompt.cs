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
			   references for the format, not templates; when creating a process, fork one with `forkWorkflow`
			   only when it already matches what the user described.
			3. A change goes in the winning file: the entry `listWorkflows` shows with no `shadowedBy`. A new workflow
			   goes in `<id>.yaml` in the personal folder `listWorkflows` reports, or its repository folder if the
			   user says the process belongs to this repository; never assume a folder. The `id` must equal the file
			   name. To change a built-in, or a personal workflow for this repository only, call `forkWorkflow`
			   first and change the copy, forking a built-in to `personal` unless the user says it belongs to this
			   repository; never edit a built-in in place. A fork keeps its id; a variant under a new id is a new
			   workflow, not a fork.
			4. Prefer the user's existing skills as stage steps (`skill` + `args`) over restating them as prompts.
			   Choose `run.context` deliberately: `continue` (default) works in the main conversation, `fork`
			   gives a separate stage the main conversation's history, and `fresh` receives only its stage
			   instructions and explicit inputs. Use fork for reviews needing context and fresh for independent
			   judgment; both keep main available and return outputs and a summary to it. They use the session's
			   provider; a fresh stage is not a way to switch providers.
			5. Route with `then`: a stage id or `done` to go there, or a decision (`ask` + `choices`, each answer
			   naming the stage it leads to). Stages without `then` move to the following stage. Use `if` to run
			   a stage only when a yes/no question says so; a skipped stage falls through to the following one.
			6. Approvals are decisions the user answers: `by: user`, with choices such as `approve` → the next
			   stage and `revise` → this stage (an answer leading back to the stage reopens it with the user's
			   feedback). Put one wherever the user wants to sign off.
			7. Write questions around observable criteria, one concern per question. A decision answered by
			   the model (`by: model`, the default) runs on a model with no tools — it
			   may be a small decision model. It sees only its `ask` text with the `{{…}}` values filled in, the
			   stage's outputs, and the summary the agent gives at `completeStage`, including user feedback. When
			   `ask` references a `files` input or output, Weavie supplies its current text as separate evidence.
			   Any unreadable file sends the whole question to the user without a model call. Literal paths, paths
			   assembled from text, unreferenced files, URLs, and document links cause no reads. The model has no
			   commands or conversation history. Shape outputs for the decisions that read them, and explicitly
			   reference every needed file value. The model returns a choice with numeric confidence. Below
			   `workflow.decisionConfidence`
			   (default 0.8), refusal, failure, or an invalid answer asks the user. Model `if` and `then` may set
			   `confidence:` to override the threshold; user decisions cannot. No implicit `unsure` answer exists.
			   Choice values may be a destination or `{ then: stage, description: "plain-text criteria" }`;
			   descriptions are recommended to distinguish choices.
			8. Put `limit` on every model decision that can loop back: how many times in a row the model may
			   answer it before the user is asked.
			9. Never ask a model about something the run already knows, such as whether a stage was skipped or
			   an output exists; stage order and fixed `then` targets handle those.
			10. Declare `outputs` for anything a later stage needs, and reference it as `{{<stage>.<output>}}`;
			   reference run inputs as `{{inputs.<name>}}`. A stage visited more than once overwrites its outputs,
			   so anything a later stage needs from every visit (such as each group's videos) must be written to a
			   file that persists, and the later stage reads it from there. Use `{{run.documents}}` for documents
			   shared across sessions: Weavie creates this folder outside worktrees; new and forked sessions inherit it,
			   including their workflow runs. No path input is needed. Report its files as absolute `files` outputs
			   when decisions need their contents. Documents survive completion and cancellation. Weavie deletes them when the last referencing
			   session is deleted or replaces its run; child sessions keep shared documents alive.
			11. Only put commands and flags in prompts that you've verified exist (check `--help`), and make sure
			   every file or value a stage uses is produced by an earlier stage and reachable where it's used.
			12. Before finishing, walk through a run stage by stage: what each stage receives, what it reports,
			   and what each decision can see.
			13. Before saving any new or changed workflow, pass the complete proposed YAML to `validateWorkflow` as
			   `text`. Fix every error and validate again until it reports `Valid.`, because later checks and all
			   warnings only run once structural errors are gone. Warnings are flags for judgment; you may ignore
			   intentional warnings. They never block saving or running. Flags identify unreferenced `files`
			   outputs and literal/composed paths that supply no file contents. Then save with your normal edit
			   or write tool, not a script.
			14. Summarize the workflow's stages for the user and say where the file lives.
			""",
	};
}
