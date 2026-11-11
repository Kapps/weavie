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
