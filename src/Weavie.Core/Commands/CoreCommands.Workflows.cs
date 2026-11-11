namespace Weavie.Core.Commands;

// Workflow runs. Every command but Run takes the run id it acts on. See docs/specs/workflows.md.
public static partial class CoreCommands {
	/// <summary>Starts a workflow in the invoking session.</summary>
	public const string WorkflowRun = "weavie.workflow.run";

	/// <summary>Answers the question a run is asking the user.</summary>
	public const string WorkflowAnswer = "weavie.workflow.answer";

	/// <summary>Resumes a paused run.</summary>
	public const string WorkflowResume = "weavie.workflow.resume";

	/// <summary>Moves a run to a stage.</summary>
	public const string WorkflowJumpToStage = "weavie.workflow.jumpToStage";

	/// <summary>Cancels a run.</summary>
	public const string WorkflowCancel = "weavie.workflow.cancel";

	/// <summary>Sets or clears what fills a workflow role in one provider's sessions.</summary>
	public const string WorkflowSetRole = "weavie.workflow.setRole";

	private const string RunArg = "\"run\":{\"type\":\"string\",\"description\":\"The run id from the workflow stage block\"}";

	private static void RegisterWorkflows(CommandRegistry registry) {
		foreach (var (id, title, description, args) in new[] {
			(WorkflowRun, "Run Workflow", "Start a Weavie workflow in this session. 'workflow' is an id from listWorkflows; 'prompt' is the request the run reads as "
				+ "{{run.prompt}}; 'inputs' maps each declared input to its value.",
				"\"workflow\":{\"type\":\"string\"},\"prompt\":{\"type\":\"string\"},\"inputs\":{\"type\":\"object\"}"),
			(WorkflowAnswer, "Answer Workflow Question", "Give the user's answer to the question a workflow run is asking. Only when the user explicitly answers; never answer for them.",
				RunArg + ",\"choice\":{\"type\":\"string\",\"description\":\"One of the question's answers\"},"
				+ "\"feedback\":{\"type\":\"string\",\"description\":\"The user's own words with their answer; empty when they gave none\"}"),
			(WorkflowResume, "Resume Workflow", "Resume a paused workflow run: a paused question is asked again, otherwise the stage restarts.", RunArg),
			(WorkflowJumpToStage, "Jump to Workflow Stage", "Move a workflow run to a stage, which opens regardless of its if.",
				RunArg + ",\"stage\":{\"type\":\"string\"}"),
			(WorkflowCancel, "Cancel Workflow", "Cancel a workflow run.", RunArg),
			(WorkflowSetRole, "Set Workflow Role", "Set or clear, for every workspace, which model fills a workflow role (a step's 'role', e.g. planner) "
				+ "in one provider's sessions. listWorkflows shows what is saved; listAgents gives the provider and model ids. Pass exactly one "
				+ "of 'agent' or 'clear'.",
				"\"role\":{\"type\":\"string\",\"description\":\"The role name: lowercase words joined by hyphens\"},"
				+ "\"provider\":{\"type\":\"string\",\"description\":\"The session provider id this applies to, e.g. claude-acp\"},"
				+ "\"agent\":{\"type\":\"object\",\"description\":\"What fills the role: provider (the session provider), model (a model id it advertises), "
				+ "and optional options (config option id to value id)\",\"properties\":{\"provider\":{\"type\":\"string\"},\"model\":{\"type\":\"string\"},"
				+ "\"options\":{\"type\":\"object\"}},\"required\":[\"provider\",\"model\"]},"
				+ "\"clear\":{\"type\":\"boolean\",\"description\":\"true to remove the saved role\"}"),
		}) {
			registry.Register(new CommandDefinition {
				Id = id,
				Title = title,
				RunsIn = CommandLocation.Core,
				OnTimeout = OperationSeverity.ActionFailed,
				Category = "Workflow",
				Description = description,
				ShowInPalette = false,
				ArgsSchemaJson = "{" + args + "}",
			});
		}
	}
}
