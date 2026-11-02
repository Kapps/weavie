using System.Text.Json;
using Weavie.Core.Mcp;

namespace Weavie.Core.Workflows;

/// <summary>The workflow capability the registry MCP server exposes to a session's agent.</summary>
public interface IWorkflowRegistry {
	/// <summary>Every workflow file visible to the session, including shadowed and broken ones.</summary>
	IReadOnlyList<WorkflowCatalogEntry> List();

	/// <summary>The problems in the workflow file at <paramref name="path"/> (relative to the worktree); empty when valid.</summary>
	IReadOnlyList<WorkflowDiagnostic> Validate(string path);

	/// <summary>Completes the session's open stage with <paramref name="outputs"/>; returns the reply for the agent.</summary>
	string CompleteStage(JsonElement outputs);

	/// <summary>Reports that the agent can't finish the session's open stage.</summary>
	void FailStage(string reason);
}

/// <summary>Workflow registries for servers that have none, or that only author workflows.</summary>
public static class WorkflowRegistry {
	/// <summary>A registry for MCP servers that never advertise the workflow tools; every call is a tool error.</summary>
	public static IWorkflowRegistry None { get; } = new Unavailable();

	/// <summary>A registry that lists and validates <paramref name="catalog"/>'s workflows but has no runs.</summary>
	public static IWorkflowRegistry AuthoringOnly(WorkflowCatalog catalog) => new Authoring(catalog);

	private sealed class Unavailable : IWorkflowRegistry {
		public IReadOnlyList<WorkflowCatalogEntry> List() => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> Validate(string path) => throw Error();

		public string CompleteStage(JsonElement outputs) => throw Error();

		public void FailStage(string reason) => throw Error();

		private static ToolUnavailableException Error() => new("Workflows are not available.");
	}

	private sealed class Authoring(WorkflowCatalog catalog) : IWorkflowRegistry {
		public IReadOnlyList<WorkflowCatalogEntry> List() => catalog.List();

		public IReadOnlyList<WorkflowDiagnostic> Validate(string path) => catalog.Validate(path);

		public string CompleteStage(JsonElement outputs) => throw NoRun();

		public void FailStage(string reason) => throw NoRun();

		private static WorkflowException NoRun() => new("No workflow run is live in this session.");
	}
}

/// <summary>A session's registry: its catalog plus its live run.</summary>
public sealed class SessionWorkflowRegistry(WorkflowCatalog catalog, WorkflowRunner runner) : IWorkflowRegistry {
	/// <inheritdoc/>
	public IReadOnlyList<WorkflowCatalogEntry> List() => catalog.List();

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> Validate(string path) => catalog.Validate(path);

	/// <inheritdoc/>
	public string CompleteStage(JsonElement outputs) => runner.CompleteStage(outputs);

	/// <inheritdoc/>
	public void FailStage(string reason) => runner.FailStage(reason);
}
