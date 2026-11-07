using System.Text.Json;
using Weavie.Core.Json;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Core.Mcp;

// Workflow tools: authoring (list, schema, validate) and the session's run (complete or fail its open stage).
// See docs/specs/workflows.md.
public sealed partial class McpServer {
	/// <summary>The tool that lists the workflows visible to the session.</summary>
	public const string ListWorkflowsTool = "listWorkflows";

	/// <summary>The tool that completes the open workflow stage.</summary>
	public const string CompleteStageTool = "completeStage";

	/// <summary>The tool that reports the open workflow stage can't be finished.</summary>
	public const string FailStageTool = "failStage";

	private const string GetWorkflowSchemaTool = "getWorkflowSchema";
	private const string ValidateWorkflowTool = "validateWorkflow";

	private const string WorkflowToolEntries =
		$$$$"""
          {"name":"{{{{ListWorkflowsTool}}}}","description":"List every Weavie workflow visible to this session (built-in, personal ~/.weavie/workflows, and this repository's .weavie/workflows), with its id, description, scope, file path, inputs, whether it is valid (and its errors), and which scope shadows it when a more specific file has the same id.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{GetWorkflowSchemaTool}}}}","description":"Get the JSON Schema of Weavie workflow YAML files. Read it before writing or editing a workflow; never write fields it doesn't list.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{ValidateWorkflowTool}}}}","description":"Validate a Weavie workflow and get its problems with path, line, and column. Structural errors come first; the remaining checks and all warnings run once those are fixed, so re-validate until it reports Valid. Pass exactly one of 'text', the complete proposed YAML, checked without touching any file; or 'id', the saved workflow that wins for that id. Validate the proposed text and fix every problem before saving.","inputSchema":{"type":"object","properties":{"id":{"type":"string","description":"A workflow id from listWorkflows."},"text":{"type":"string","description":"The complete proposed workflow YAML."}}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{CompleteStageTool}}}}","description":"Complete the open stage of this session's Weavie workflow run, reporting the stage's declared outputs and a summary. Call it only when the stage's work is truly finished, then end your turn; Weavie starts the next stage. A rejection lists what to fix.","inputSchema":{"type":"object","properties":{"outputs":{"type":"object","description":"The stage's declared outputs by name; {} when it declares none."},"summary":{"type":"string","description":"A few sentences on what the stage did and found. The model deciding what comes next reads it, and has no other view of your work."}},"required":["outputs","summary"]}},
          {"name":"{{{{FailStageTool}}}}","description":"Report that you can't finish the open stage of this session's Weavie workflow run. The run pauses for the user with your reason.","inputSchema":{"type":"object","properties":{"reason":{"type":"string"}},"required":["reason"]}}
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
		string id = args.GetStringOrEmpty("id");
		string text = args.GetStringOrEmpty("text");
		if ((id.Length == 0) == (text.Length == 0) || args.EnumerateObject().Count() != 1) {
			await SendToolErrorAsync(responder, idRaw, $"{ValidateWorkflowTool} takes exactly one of 'id' or 'text', and nothing else.", ct).ConfigureAwait(false);
			return;
		}

		IReadOnlyList<WorkflowDiagnostic> diagnostics;
		try {
			diagnostics = id.Length > 0 ? _workflows.Validate(id) : _workflows.ValidateText(text);
		} catch (Exception ex) when (ex is WorkflowException or IOException or UnauthorizedAccessException) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		int errors = diagnostics.Count(d => !d.IsWarning);
		string verdict = errors == 0 ? "Valid." : $"Invalid: {errors} error(s).";
		await SendToolTextAsync(responder, idRaw, diagnostics.Count == 0
			? verdict
			: $"{verdict}\n{string.Join("\n", diagnostics.Select(d => $"- {d}"))}", ct).ConfigureAwait(false);
	}

	private async Task HandleCompleteStageAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string summary = args.GetStringOrEmpty("summary");
		if (summary.Length == 0) {
			await SendToolErrorAsync(responder, idRaw, $"{CompleteStageTool} requires 'summary'.", ct).ConfigureAwait(false);
			return;
		}

		string reply;
		try {
			reply = _workflows.CompleteStage(args.TryGetProperty("outputs", out var outputs) ? outputs : default, summary);
		} catch (WorkflowException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, reply, ct).ConfigureAwait(false);
	}

	private async Task HandleFailStageAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string reason = args.GetStringOrEmpty("reason");
		if (reason.Length == 0) {
			await SendToolErrorAsync(responder, idRaw, $"{FailStageTool} requires 'reason'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			_workflows.FailStage(reason);
		} catch (WorkflowException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, "The run is paused for the user. End your turn.", ct).ConfigureAwait(false);
	}

	private static string ScopeName(WorkflowScope scope) => scope switch {
		WorkflowScope.BuiltIn => "built-in",
		WorkflowScope.Personal => "personal",
		_ => "repository",
	};
}
