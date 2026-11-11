using Weavie.Core.Mcp;
using Weavie.Workflows;

namespace Weavie.Core.Workflows;

/// <summary>The workflow capability the registry MCP server exposes to a session's agent.</summary>
public interface IWorkflowRegistry {
	/// <summary>Every workflow file visible to the session, including shadowed and broken ones.</summary>
	IReadOnlyList<WorkflowCatalogEntry> List();

	/// <summary>The problems in the saved workflow that wins for <paramref name="id"/>; empty when valid.</summary>
	IReadOnlyList<WorkflowDiagnostic> Validate(string id);

	/// <summary>The problems in proposed workflow <paramref name="text"/>; no file is touched.</summary>
	IReadOnlyList<WorkflowDiagnostic> ValidateText(string text);

	/// <summary>Copies the valid workflow <paramref name="id"/> into the more specific <paramref name="scope"/>; returns the new path.</summary>
	string Fork(string id, WorkflowScope scope);

	/// <summary>The folder holding <paramref name="scope"/>'s workflow files, which may not exist yet.</summary>
	string Folder(WorkflowScope scope);

	/// <summary>Captures the exact stage visit and round for a tool call admitted from main.</summary>
	WorkflowStageCall? CaptureStageCall();

	/// <summary>The user's saved workflow roles.</summary>
	IReadOnlyList<WorkflowRoleAssignment> Roles();

}

/// <summary>The registry for MCP servers without workflows.</summary>
public static class WorkflowRegistry {
	/// <summary>A registry for MCP servers that never advertise the workflow tools; every call is a tool error.</summary>
	public static IWorkflowRegistry None { get; } = new Unavailable();

	private sealed class Unavailable : IWorkflowRegistry {
		public IReadOnlyList<WorkflowCatalogEntry> List() => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> Validate(string id) => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> ValidateText(string text) => throw Error();

		public string Fork(string id, WorkflowScope scope) => throw Error();

		public string Folder(WorkflowScope scope) => throw Error();

		public WorkflowStageCall? CaptureStageCall() => null;

		public IReadOnlyList<WorkflowRoleAssignment> Roles() => throw Error();

		private static ToolUnavailableException Error() => new("Workflows are not available.");
	}
}

/// <summary>A session's registry: its catalog, its live run, and the user's roles.</summary>
public sealed class SessionWorkflowRegistry(WorkflowCatalog catalog, WorkflowRunner runner, WorkflowRoleStore roles) : IWorkflowRegistry {
	/// <inheritdoc/>
	public IReadOnlyList<WorkflowCatalogEntry> List() => catalog.List();

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> Validate(string id) => catalog.Validate(id);

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> ValidateText(string text) => catalog.ValidateText(text);

	/// <inheritdoc/>
	public string Fork(string id, WorkflowScope scope) => catalog.Fork(id, scope);

	/// <inheritdoc/>
	public string Folder(WorkflowScope scope) => catalog.Folder(scope);

	/// <inheritdoc/>
	public WorkflowStageCall? CaptureStageCall() => runner.CaptureStageCall();

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowRoleAssignment> Roles() => roles.All();
}
