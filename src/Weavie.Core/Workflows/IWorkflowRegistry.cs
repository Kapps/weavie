using Weavie.Core.Mcp;

namespace Weavie.Core.Workflows;

/// <summary>The workflow capability the registry MCP server exposes to a session's agent.</summary>
public interface IWorkflowRegistry {
	/// <summary>Every workflow file visible to the session, including shadowed and broken ones.</summary>
	IReadOnlyList<WorkflowCatalogEntry> List();

	/// <summary>The problems in the workflow file at <paramref name="path"/> (relative to the worktree); empty when valid.</summary>
	IReadOnlyList<WorkflowDiagnostic> Validate(string path);
}

/// <summary>Workflow registries for servers that have none.</summary>
public static class WorkflowRegistry {
	/// <summary>A registry for MCP servers that never advertise the workflow tools; every call is a tool error.</summary>
	public static IWorkflowRegistry None { get; } = new Unavailable();

	private sealed class Unavailable : IWorkflowRegistry {
		public IReadOnlyList<WorkflowCatalogEntry> List() => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> Validate(string path) => throw Error();

		private static ToolUnavailableException Error() => new("Workflows are not available.");
	}
}
