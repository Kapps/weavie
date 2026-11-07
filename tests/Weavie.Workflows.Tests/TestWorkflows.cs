namespace Weavie.Workflows.Tests;

internal static class TestWorkflows {
	public static WorkflowVocabulary Words { get; } = new() {
		Host = "Host",
		CompleteStage = "completeStage",
		FailStage = "failStage",
		RunCommand = "runCommand",
		Answer = "workflow.answer",
		Resume = "workflow.resume",
		JumpToStage = "workflow.jumpToStage",
		Cancel = "workflow.cancel",
	};

	public static WorkflowCatalog Catalog(string worktree, string personal) =>
		new(worktree, personal, Path.Combine(worktree, ".weavie", "workflows"));
}
