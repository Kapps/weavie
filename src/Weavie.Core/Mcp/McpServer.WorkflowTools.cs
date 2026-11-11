using System.Text.Json;
using Weavie.Core.Json;
using Weavie.Core.Workflows;
using Weavie.Workflows;

namespace Weavie.Core.Mcp;

// Workflow tools: authoring (list, schema, validate, fork) and the session's run (complete or fail its open stage).
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
	private const string ForkWorkflowTool = "forkWorkflow";

	private const string WorkflowToolEntries =
		$$$$"""
          {"name":"{{{{ListWorkflowsTool}}}}","description":"List every Weavie workflow visible to this session (built-in, personal, and this repository's), with its id, description, scope, file path, inputs, the roles its steps ask for, whether it is valid (and its errors), and which scope shadows it when a more specific file has the same id. Also returns the personal and repository folders this host reads, so a new workflow lands where it will be found, and the user's saved roles: for each session provider, the agent filling each role.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{GetWorkflowSchemaTool}}}}","description":"Get the JSON Schema of Weavie workflow YAML files. Read it before writing or editing a workflow; never write fields it doesn't list.","inputSchema":{"type":"object","properties":{}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{ValidateWorkflowTool}}}}","description":"Validate a Weavie workflow and get its problems with path, line, and column. Structural errors come first; the remaining checks and all warnings run once those are fixed, so re-validate until it reports Valid. Pass exactly one of 'text', the complete proposed YAML, checked without touching any file; or 'id', the saved workflow that wins for that id. Validate the proposed text and fix every error before saving. Warnings are flags for judgment; intentional warnings may be ignored and never block saving or running.","inputSchema":{"type":"object","properties":{"id":{"type":"string","description":"A workflow id from listWorkflows."},"text":{"type":"string","description":"The complete proposed workflow YAML."}}},"annotations":{"readOnlyHint":true}},
          {"name":"{{{{ForkWorkflowTool}}}}","description":"Copy a valid Weavie workflow, including a built-in, into a more specific scope under the same id, and get the new file's path. It never overwrites. Fork to change a built-in, or a personal workflow for this repository only; a repository fork is an ordinary change to commit.","inputSchema":{"type":"object","properties":{"id":{"type":"string","description":"A workflow id from listWorkflows."},"scope":{"type":"string","enum":["personal","repository"]}},"required":["id","scope"]}},
          {"name":"{{{{CompleteStageTool}}}}","description":"Complete the open stage of this session's Weavie workflow run, reporting the stage's declared outputs and a summary. Call it only when the stage's work is truly finished, then end your turn; Weavie starts the next stage. A rejection lists what to fix.","inputSchema":{"type":"object","properties":{"outputs":{"type":"object","description":"The stage's declared outputs by name; {} when it declares none."},"summary":{"type":"string","description":"A few sentences on what the stage did and found. The model deciding what comes next sees this summary with user feedback, its rendered question, stage output values, and the current text of explicitly referenced files. It has no tools or conversation history."}},"required":["outputs","summary"]}},
          {"name":"{{{{FailStageTool}}}}","description":"Report that you can't finish the open stage of this session's Weavie workflow run. The run pauses for the user with your reason.","inputSchema":{"type":"object","properties":{"reason":{"type":"string"}},"required":["reason"]}}
        """;

	private Task HandleListWorkflowsAsync(IMcpResponder responder, string? idRaw, CancellationToken ct) =>
		SendToolTextAsync(responder, idRaw, JsonWrite.Object(writer => {
			writer.WriteStartObject("folders");
			writer.WriteString("personal", _workflows.Folder(WorkflowScope.Personal));
			writer.WriteString("repository", _workflows.Folder(WorkflowScope.Repository));
			writer.WriteEndObject();
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
					writer.WriteStartArray("roles");
					foreach (string role in definition.Roles) writer.WriteStringValue(role);
					writer.WriteEndArray();
				} else {
					writer.WriteStartArray("errors");
					foreach (var diagnostic in entry.Diagnostics) writer.WriteStringValue(diagnostic.ToString());
					writer.WriteEndArray();
				}

				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteStartArray("roles");
			foreach (var saved in _workflows.Roles()) {
				writer.WriteStartObject();
				writer.WriteString("role", saved.Role);
				writer.WriteString("provider", saved.Provider);
				writer.WriteStartObject("agent");
				writer.WriteString("provider", saved.Agent.Provider);
				writer.WriteString("model", saved.Agent.Model);
				writer.WriteStartObject("options");
				foreach (var (option, value) in saved.Agent.Options) writer.WriteString(option, value);
				writer.WriteEndObject();
				writer.WriteEndObject();
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
		} catch (Exception ex) when (IsWorkflowRefusal(ex)) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		int errors = diagnostics.Count(d => !d.IsWarning);
		string verdict = errors == 0 ? "Valid." : $"Invalid: {errors} error(s).";
		await SendToolTextAsync(responder, idRaw, diagnostics.Count == 0
			? verdict
			: $"{verdict}\n{string.Join("\n", diagnostics.Select(d => $"- {d}"))}", ct).ConfigureAwait(false);
	}

	private async Task HandleForkWorkflowAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string id = args.GetStringOrEmpty("id");
		WorkflowScope? scope = args.GetStringOrEmpty("scope") switch {
			"personal" => WorkflowScope.Personal,
			"repository" => WorkflowScope.Repository,
			_ => null,
		};
		if (id.Length == 0 || scope is not { } target) {
			await SendToolErrorAsync(responder, idRaw, $"{ForkWorkflowTool} requires 'id' and a 'scope' of personal or repository.", ct).ConfigureAwait(false);
			return;
		}

		string path;
		try {
			path = _workflows.Fork(id, target);
		} catch (Exception ex) when (IsWorkflowRefusal(ex)) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, $"Forked {id} to {path}.", ct).ConfigureAwait(false);
	}

	private async Task HandleCompleteStageAsync(IMcpResponder responder, JsonElement args, string? idRaw, WorkflowStageCall? stage, CancellationToken ct) {
		string summary = args.GetStringOrEmpty("summary");
		if (summary.Length == 0) {
			await SendToolErrorAsync(responder, idRaw, $"{CompleteStageTool} requires 'summary'.", ct).ConfigureAwait(false);
			return;
		}

		string reply;
		try {
			reply = (stage ?? throw new WorkflowException("This conversation does not own an open workflow stage.")).Complete(args.TryGetProperty("outputs", out var outputs) ? outputs : default, summary);
		} catch (WorkflowException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, reply, ct).ConfigureAwait(false);
	}

	private async Task HandleFailStageAsync(IMcpResponder responder, JsonElement args, string? idRaw, WorkflowStageCall? stage, CancellationToken ct) {
		string reason = args.GetStringOrEmpty("reason");
		if (reason.Length == 0) {
			await SendToolErrorAsync(responder, idRaw, $"{FailStageTool} requires 'reason'.", ct).ConfigureAwait(false);
			return;
		}

		try {
			(stage ?? throw new WorkflowException("This conversation does not own an open workflow stage.")).Fail(reason);
		} catch (WorkflowException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}

		await SendToolTextAsync(responder, idRaw, "The run is paused for the user. End your turn.", ct).ConfigureAwait(false);
	}

	private static bool IsWorkflowRefusal(Exception ex) => ex is WorkflowException or IOException or UnauthorizedAccessException;

	private static string ScopeName(WorkflowScope scope) => scope switch {
		WorkflowScope.BuiltIn => "built-in",
		WorkflowScope.Personal => "personal",
		_ => "repository",
	};
}
