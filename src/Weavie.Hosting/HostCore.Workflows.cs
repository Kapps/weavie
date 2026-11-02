using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Workflows;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A session's workflow run: its run commands, and the run line published on the session's `status` feature.
// See docs/specs/workflows.md.
public sealed partial class HostCore {
	private static readonly JsonElement EmptyInputs = JsonDocument.Parse("{}").RootElement.Clone();

	private void WireWorkflow(HostSession session) {
		var runner = session.Workflow;
		session.Commands.RegisterHandler(CoreCommands.WorkflowRun, (argsJson, _) => RunWorkflowCommand(args => {
			var run = runner.Start(Required(args, "workflow"), args.TryGetProperty("inputs", out var inputs) ? inputs : EmptyInputs);
			return $"Started run {run.Id} of workflow {run.WorkflowId} at stage {run.Stage}.";
		}, argsJson));
		RegisterRunCommand(session, CoreCommands.WorkflowApproveStage, (run, _) => runner.Approve(run), "Approved the stage.");
		RegisterRunCommand(session, CoreCommands.WorkflowRejectStage,
			(run, args) => runner.Reject(run, Required(args, "feedback")), "Sent the feedback; the stage reopens.");
		RegisterRunCommand(session, CoreCommands.WorkflowResume, (run, _) => runner.Resume(run), "Resumed the run.");
		RegisterRunCommand(session, CoreCommands.WorkflowJumpToStage,
			(run, args) => runner.Jump(run, Required(args, "stage")), "Moved the run.");
		RegisterRunCommand(session, CoreCommands.WorkflowCancel, (run, _) => runner.Cancel(run), "Cancelled the run.");

		SessionRunWire? published = null;
		void Publish() => PostForSession(session, () => {
			var line = SessionRunLine(session);
			if (line == published) return;
			published = line;
			PostSessionRun(session.Bus.BroadcastTarget, line);
		});
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

	// Refused before any worktree exists: an unknown workflow or bad inputs must not leave a session behind.
	private CommandResult? WorkflowStartError(SessionSlot? source, WorkflowStart start) {
		string root = source?.WorktreePath ?? WorkspaceRoot;
		try {
			WorkflowTransitions.ReadInputs(new WorkflowCatalog(root, WeaviePaths.WorkflowsDir).Resolve(start.Id), start.Inputs, root);
			return null;
		} catch (WorkflowException ex) {
			return CommandResult.Failure(ex.Message);
		}
	}

	private static SessionRunWire? SessionRunLine(HostSession session) =>
		WorkflowRunSummary.From(session.Workflow.Current, session.Status.Status) is { } summary
			? new SessionRunWire(summary.RunId, summary.Workflow, summary.Stage, JsonNamingPolicy.CamelCase.ConvertName(summary.State.ToString()))
			: null;

	private static void PostSessionRun(MessageTarget target, SessionRunWire? run) =>
		target.Feature("status").Publish("run", WireJson.Default.SessionRunChanged, new(run));

	private sealed record WorkflowStart(string Id, JsonElement Inputs);
}

/// <summary>A session's live workflow run as its session-list line shows it.</summary>
internal sealed record SessionRunWire(string Id, string Workflow, string Stage, string State);

/// <summary>A session's run line changed; <see langword="null"/> when no run is live.</summary>
internal sealed record SessionRunChanged(SessionRunWire? Run);
