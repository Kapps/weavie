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
	};

	public static WorkflowTransitions Machine(WorkflowDefinition definition) => new(definition, Environment());

	public static WorkflowTransitions Machine(WorkflowDefinition definition, string worktree) =>
		new(definition, Environment(worktree, Path.GetTempPath()));

	public static WorkflowFileScope Files(string worktree) => new(worktree, null);

	public static WorkflowCatalog Catalog(string worktree, string personal) =>
		new(worktree, personal, Path.Combine(worktree, ".weavie", "workflows"));
}
