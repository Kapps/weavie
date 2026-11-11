using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>How Weavie hosts the workflow engine: the names it writes for agents and where workflow files live.</summary>
public static class WeavieWorkflows {
	/// <summary>Weavie's tool and command names, from their one definition.</summary>
	public static WorkflowVocabulary Vocabulary { get; } = new() {
		Host = "Weavie",
		CompleteStage = McpServer.CompleteStageTool,
		FailStage = McpServer.FailStageTool,
		RunCommand = McpServer.RunCommandTool,
		OpenFile = McpServer.OpenFileTool,
		Answer = CoreCommands.WorkflowAnswer,
		Resume = CoreCommands.WorkflowResume,
		JumpToStage = CoreCommands.WorkflowJumpToStage,
		Cancel = CoreCommands.WorkflowCancel,
	};

	/// <summary>
	/// A runner's environment in <paramref name="worktree"/>: Weavie's names, the live workflow settings, and each
	/// run's <paramref name="documentsDirectory"/> from its id.
	/// </summary>
	public static WorkflowEnvironment Environment(SettingsStore settings, string worktree, Func<string, string> documentsDirectory) {
		ArgumentNullException.ThrowIfNull(settings);
		return new() {
			Worktree = worktree,
			Words = Vocabulary,
			DocumentsDirectory = documentsDirectory,
			MaxStageAttempts = () => (int)settings.RequireInt(WorkflowSettings.MaxStageAttempts),
			OpenReviewItem = () => settings.RequireBool(WorkflowSettings.OpenReviewItem),
			DecisionConfidence = () => settings.RequireNumber(WorkflowSettings.DecisionConfidence),
		};
	}

	/// <summary>The workflows visible from <paramref name="worktree"/>: built-in, the user's, and the repository's.</summary>
	public static WorkflowCatalog Catalog(string worktree) =>
		new(worktree, WeaviePaths.WorkflowsDir, WeaviePaths.RepositoryWorkflowsDir(worktree));
}
