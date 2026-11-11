using Weavie.Core.Commands;
using Weavie.Core.Mcp;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>How Weavie hosts the workflow engine: the names it writes for agents and where workflow files live.</summary>
public static class WeavieWorkflows {
	/// <summary>The registered provider <paramref name="id"/>; throws <see cref="WorkflowException"/> naming the registered ones otherwise.</summary>
	public static Agents.AgentProviderInfo RequireProvider(Agents.AgentProviderRegistry providers, string id) {
		ArgumentNullException.ThrowIfNull(providers);
		return providers.FindInfo(id) ?? throw new WorkflowException(
			$"No agent provider '{id}' is registered; the providers are {string.Join(", ", providers.Providers.Select(p => p.Info.Id))}.");
	}

	/// <summary>The non-empty text argument <paramref name="name"/> of a workflow command; throws <see cref="WorkflowException"/> otherwise.</summary>
	public static string Required(System.Text.Json.JsonElement args, string name) =>
		args.ValueKind == System.Text.Json.JsonValueKind.Object && args.TryGetProperty(name, out var value)
			&& value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } text
			? text
			: throw new WorkflowException($"'{name}' is required.");

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

	/// <summary>The workflows visible from <paramref name="worktree"/>: built-in, the user's, and the repository's.</summary>
	public static WorkflowCatalog Catalog(string worktree) =>
		new(worktree, WeaviePaths.WorkflowsDir, WeaviePaths.RepositoryWorkflowsDir(worktree));
}
