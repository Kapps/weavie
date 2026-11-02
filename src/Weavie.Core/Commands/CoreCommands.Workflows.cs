namespace Weavie.Core.Commands;

// Workflow runs. Every command but Run takes the run id it acts on. See docs/specs/workflows.md.
public static partial class CoreCommands {
	/// <summary>Starts a workflow in the invoking session.</summary>
	public const string WorkflowRun = "weavie.workflow.run";

	/// <summary>Approves the stage awaiting approval.</summary>
	public const string WorkflowApproveStage = "weavie.workflow.approveStage";

	/// <summary>Rejects the stage awaiting approval with feedback.</summary>
	public const string WorkflowRejectStage = "weavie.workflow.rejectStage";

	/// <summary>Resumes a paused run.</summary>
	public const string WorkflowResume = "weavie.workflow.resume";

	/// <summary>Moves a run to a stage.</summary>
	public const string WorkflowJumpToStage = "weavie.workflow.jumpToStage";

	/// <summary>Cancels a run.</summary>
	public const string WorkflowCancel = "weavie.workflow.cancel";

	private const string RunArg = "\"run\":{\"type\":\"string\",\"description\":\"The run id from the workflow stage block\"}";

	private static void RegisterWorkflows(CommandRegistry registry) {
		foreach (var (id, title, description, args) in new[] {
			(WorkflowRun, "Run Workflow", "Start a Weavie workflow in this session. 'workflow' is an id from listWorkflows; 'inputs' maps each declared input to its value.",
				"\"workflow\":{\"type\":\"string\"},\"inputs\":{\"type\":\"object\"}"),
			(WorkflowApproveStage, "Approve Workflow Stage", "Approve the workflow stage awaiting approval. Only when the user explicitly approves.", RunArg),
			(WorkflowRejectStage, "Reject Workflow Stage", "Reject the workflow stage awaiting approval; the stage reopens with the user's feedback.",
				RunArg + ",\"feedback\":{\"type\":\"string\",\"description\":\"What the user wants changed\"}"),
			(WorkflowResume, "Resume Workflow", "Resume a paused workflow run: a paused decision is asked again, otherwise the stage restarts.", RunArg),
			(WorkflowJumpToStage, "Jump to Workflow Stage", "Move a workflow run to a stage, which opens regardless of its condition.",
				RunArg + ",\"stage\":{\"type\":\"string\"}"),
			(WorkflowCancel, "Cancel Workflow", "Cancel a workflow run.", RunArg),
		}) {
			registry.Register(new CommandDefinition {
				Id = id,
				Title = title,
				RunsIn = CommandLocation.Core,
				Category = "Workflow",
				Description = description,
				ShowInPalette = false,
				ArgsSchemaJson = "{" + args + "}",
			});
		}
	}
}
