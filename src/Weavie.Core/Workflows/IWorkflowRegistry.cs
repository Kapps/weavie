using System.Text.Json;
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

	/// <summary>Completes the session's open stage with <paramref name="outputs"/> and a <paramref name="summary"/>; returns the reply for the agent.</summary>
	string CompleteStage(JsonElement outputs, string summary);

	/// <summary>Reports that the agent can't finish the session's open stage.</summary>
	void FailStage(string reason);
}

/// <summary>The registry for MCP servers without workflows.</summary>
public static class WorkflowRegistry {
	/// <summary>A registry for MCP servers that never advertise the workflow tools; every call is a tool error.</summary>
	public static IWorkflowRegistry None { get; } = new Unavailable();

	private sealed class Unavailable : IWorkflowRegistry {
		public IReadOnlyList<WorkflowCatalogEntry> List() => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> Validate(string id) => throw Error();

		public IReadOnlyList<WorkflowDiagnostic> ValidateText(string text) => throw Error();

		public string CompleteStage(JsonElement outputs, string summary) => throw Error();

		public void FailStage(string reason) => throw Error();

		private static ToolUnavailableException Error() => new("Workflows are not available.");
	}
}

/// <summary>A session's registry: its catalog plus its live run.</summary>
public sealed class SessionWorkflowRegistry(WorkflowCatalog catalog, WorkflowRunner runner) : IWorkflowRegistry {
	/// <inheritdoc/>
	public IReadOnlyList<WorkflowCatalogEntry> List() => catalog.List();

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> Validate(string id) => catalog.Validate(id);

	/// <inheritdoc/>
	public IReadOnlyList<WorkflowDiagnostic> ValidateText(string text) => catalog.ValidateText(text);

	/// <inheritdoc/>
	public string CompleteStage(JsonElement outputs, string summary) => runner.CompleteStage(outputs, summary);

	/// <inheritdoc/>
	public void FailStage(string reason) => runner.FailStage(reason);
}
