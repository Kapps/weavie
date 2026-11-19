namespace Weavie.Workflows.Tests;

internal static class TestWorkflows {
	public static WorkflowVocabulary Words { get; } = new() {
		Host = "Host",
		CompleteStage = "completeStage",
		FailStage = "failStage",
		RunCommand = "runCommand",
		OpenFile = "openFile",
		Answer = "workflow.answer",
		Resume = "workflow.resume",
		JumpToStage = "workflow.jumpToStage",
		Cancel = "workflow.cancel",
	};

	// A new context value or start field lands here once, not at every test's call site.
	public static WorkflowEnvironment Environment() => Environment(Path.GetTempPath(), Path.GetTempPath());

	public static WorkflowEnvironment Environment(string worktree, string documents) => new() {
		Worktree = worktree,
		Words = Words,
		DocumentsDirectory = id => Path.Combine(documents, id),
		MaxStageAttempts = () => 3,
		OpenReviewItem = () => true,
		DecisionConfidence = () => 0.8,
	};

	public static WorkflowRunStart Start => new() {
		RunId = "r-1",
		SourcePath = "p",
		Inputs = [],
		DocumentsDirectory = Path.GetTempPath(),
		Hold = null,
		Prompt = WorkflowPrompt.None,
		Roles = [],
	};

	public static WorkflowTransitions Machine(WorkflowDefinition definition) => new(definition, Environment());

	public static WorkflowTransitions Machine(WorkflowDefinition definition, string worktree) =>
		new(definition, Environment(worktree, Path.GetTempPath()));

	public static WorkflowFileScope Files(string worktree) => new(worktree, null);

	public static WorkflowCatalog Catalog(string worktree, string personal) =>
		new(worktree, personal, Path.Combine(worktree, ".weavie", "workflows"));

	/// <summary>Fills no role: a definition that names one is refused.</summary>
	public static FixedRoles NoRoles { get; } = new([]);

	public static WorkflowLaunch Prepare(this WorkflowCatalog catalog, string id, System.Text.Json.JsonElement inputs) =>
		catalog.Prepare(id, inputs, null, WorkflowStartPrompt.None, NoRoles);
}

/// <summary>Fills each role from <paramref name="agents"/>; a role none of them names is refused.</summary>
internal sealed class FixedRoles(IReadOnlyList<WorkflowAgent> agents) : IWorkflowRoleResolver {
	public IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition) =>
		definition.Roles.FirstOrDefault(role => agents.All(agent => agent.Name != role)) is { } missing
			? throw new WorkflowException($"role \"{missing}\" has no agent")
			: agents;
}

/// <summary>The root record's state under the names the run used before it held records, and answers by the open question.</summary>
internal static class WorkflowRunTestExtensions {
	extension(WorkflowRun run) {
		public Dictionary<string, Dictionary<string, WorkflowValue>> Outputs => run.Frames[0].Outputs;
		public string Unreported => run.Frames[0].Owed.GetValueOrDefault("", "");
		public Dictionary<string, int> Streaks => run.Frames[0].Streaks;
	}

	public static void Answer(this WorkflowRunner runner, string runId, string choice, string feedback) =>
		runner.Answer(runId, runner.Current?.Asked?.Id ?? 0, choice, feedback);

	public static WorkflowStep Answer(this WorkflowTransitions machine, WorkflowRun run, string choice, string feedback) =>
		machine.Answer(run, run.Asked?.Id ?? 0, choice, feedback);
}
