using System.Text.Json;
using Weavie.Core.Json;
using Weavie.Core.Workflows;

namespace Weavie.Core.Mcp;

// Workflow authoring tools: list the visible workflows, publish the file schema, and validate a file.
// See docs/specs/workflows.md.
public sealed partial class McpServer {
	private const string WorkflowToolEntries =
		"""
          {"name":"listWorkflows","description":"List every Weavie workflow visible to this session (built-in, personal ~/.weavie/workflows, and this repository's .weavie/workflows), with its id, description, scope, file path, inputs, whether it is valid (and its errors), and which scope shadows it when a more specific file has the same id.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"getWorkflowSchema","description":"Get the JSON Schema of Weavie workflow YAML files. Read it before writing or editing a workflow; never write fields it doesn't list.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"validateWorkflow","description":"Validate a workflow YAML file and get every problem with its path, line, and column. Call it after writing or editing a workflow and fix every problem before finishing.","inputSchema":{"type":"object","properties":{"path":{"type":"string","description":"Absolute path, or relative to the worktree."}},"required":["path"]},"annotations":{"readOnlyHint":true}}
        """;

	private Task HandleListWorkflowsAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) =>
		SendToolTextAsync(responder, idRaw, JsonWrite.Object(writer => {
			writer.WriteStartArray("workflows");
			foreach (var entry in _workflows.List()) {
				writer.WriteStartObject();
				writer.WriteString("id", entry.Id);
				writer.WriteString("scope", ScopeName(entry.Scope));
				writer.WriteString("path", entry.Path);
				writer.WriteBoolean("valid", entry.Definition is not null);
				if (entry.ShadowedBy is { } shadowedBy) {
					writer.WriteString("shadowedBy", ScopeName(shadowedBy));
				}

				if (entry.Definition is { } definition) {
					writer.WriteString("description", definition.Description);
					writer.WriteStartObject("inputs");
					foreach (var input in definition.Inputs) {
						writer.WriteString(input.Name, $"{input.Type.ToString().ToLowerInvariant()}{(input.Optional ? "?" : "")}");
					}

					writer.WriteEndObject();
				} else {
					writer.WriteStartArray("errors");
					foreach (var diagnostic in entry.Diagnostics) writer.WriteStringValue(diagnostic.ToString());
					writer.WriteEndArray();
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
		}), ct);

	private async Task HandleValidateWorkflowAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string path = args.GetStringOrEmpty("path");
		if (path.Length == 0) {
			await SendToolErrorAsync(responder, idRaw, "validateWorkflow requires 'path'.", ct).ConfigureAwait(false);
			return;
		}

		IReadOnlyList<WorkflowDiagnostic> diagnostics;
		try {
			diagnostics = _workflows.Validate(path);
		} catch (Exception ex) when (ex is WorkflowException or IOException or UnauthorizedAccessException) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, diagnostics.Count == 0
			? "Valid."
			: $"{diagnostics.Count} problem(s):\n{string.Join("\n", diagnostics.Select(d => $"- {d}"))}", ct).ConfigureAwait(false);
	}

	private static string ScopeName(WorkflowScope scope) => scope switch {
		WorkflowScope.BuiltIn => "built-in",
		WorkflowScope.Personal => "personal",
		_ => "repository",
	};
}
