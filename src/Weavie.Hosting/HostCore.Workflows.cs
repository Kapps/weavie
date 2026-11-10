using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Json;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;
using Weavie.Workflows;

namespace Weavie.Hosting;

// A session's workflow run: its run commands, and the run line published on the session's `status` feature.
// See docs/specs/workflows.md.
public sealed partial class HostCore {
	private static readonly JsonElement EmptyInputs = JsonDocument.Parse("{}").RootElement.Clone();

	private void WireWorkflow(HostSession session) {
		var runner = session.Workflow;
		session.Commands.RegisterHandler(CoreCommands.WorkflowRun, (argsJson, _) => RunWorkflowCommand(args => {
			var run = runner.Start(session.WorkflowCatalog.Prepare(Required(args, "workflow"), args.TryGetProperty("inputs", out var inputs) ? inputs : EmptyInputs, session.InheritedWorkflowDocuments));
			return $"Started run {run.Id} of workflow {run.WorkflowId} at stage {run.Stage}.";
		}, argsJson));
		RegisterRunCommand(session, CoreCommands.WorkflowAnswer,
			(run, args) => runner.Answer(run, Required(args, "choice"), args.GetStringOrEmpty("feedback")), "Answered; the run continues.");
		RegisterRunCommand(session, CoreCommands.WorkflowResume, (run, _) => runner.Resume(run), "Resumed the run.");
		RegisterRunCommand(session, CoreCommands.WorkflowJumpToStage,
			(run, args) => runner.Jump(run, Required(args, "stage")), "Moved the run.");
		RegisterRunCommand(session, CoreCommands.WorkflowCancel, (run, _) => runner.Cancel(run), "Cancelled the run.");

		SessionRunChanged? published = null;
		void Publish() => PostForSession(session, () => {
			var line = SessionRunLine(session);
			if (line == published) return;
			published = line;
			PostSessionRun(session.Bus.BroadcastTarget, line);
		});
		runner.Changed += () => _workflowDocuments.Bind(session.WorkspaceRoot, session.WorkflowDocumentsDirectory);
		runner.Changed += Publish;
		session.Status.Changed += _ => Publish();
	}

	private static void RegisterRunCommand(
		HostSession session, string command, Action<string, JsonElement> act, string success) =>
		session.Commands.RegisterHandler(command, (argsJson, _) => RunWorkflowCommand(args => {
			act(Required(args, "run"), args);
			return success;
		}, argsJson));

	private static Task<CommandResult> RunWorkflowCommand(Func<JsonElement, string> act, string? argsJson) {
		try {
			using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
			return Task.FromResult(CommandResult.Success(act(args.RootElement)));
		} catch (Exception ex) when (ex is WorkflowException or JsonException) {
			return Task.FromResult(CommandResult.Failure(ex.Message));
		}
	}

	private static string Required(JsonElement args, string name) =>
		args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
			&& value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
			? text
			: throw new WorkflowException($"'{name}' is required.");

	// Resolved once, from where the user started the session, before any worktree exists: an unknown workflow or bad
	// inputs must not leave a session behind, and the new session runs exactly what was checked.
	private WorkflowLaunch PrepareWorkflow(SessionSlot? source, string workflow, JsonElement inputs, string? documentsDirectory) =>
		WeavieWorkflows.Catalog(source?.WorktreePath ?? WorkspaceRoot).Prepare(workflow, inputs, documentsDirectory);

	// A deleted session's run must not resurface in a later session on the same branch (and so the same path).
	private static void ForgetWorkflowRun(string worktreePath) {
		string run = WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(worktreePath));
		if (File.Exists(run)) File.Delete(run);
	}

	private static SessionRunChanged SessionRunLine(HostSession session) =>
		new(
			WorkflowRunSummary.From(session.Workflow.Current, session.Status.Status) is { } summary
				? new SessionRunWire(summary.RunId, summary.Workflow, summary.Stage, summary.State, summary.Reason)
				: null,
			session.Workflow.Problem ?? "");

	private static void PostSessionRun(MessageTarget target, SessionRunChanged line) =>
		target.Feature("status").Publish("run", WireJson.Default.SessionRunChanged, line);
}

/// <summary>A session's live workflow run as its session-list line shows it; <c>Reason</c> says why it paused.</summary>
internal sealed record SessionRunWire(string Id, string Workflow, string Stage, WorkflowRunState State, string Reason);

/// <summary>
/// A session's run line changed: its live run (<see langword="null"/> when none) and why its saved run couldn't be
/// loaded (empty when it could).
/// </summary>
internal sealed record SessionRunChanged(SessionRunWire? Run, string Problem);
