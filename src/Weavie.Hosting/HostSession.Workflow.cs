using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// The session's workflow run: Weavie owns its stages and delivers each stage's turn; the agent does the work and
// reports back through the session's registry. See docs/specs/workflows.md.
public sealed partial class HostSession {
	private IdleTurnDelivery? _workflowTurns;
	private int _runtimeActivated;

	/// <summary>The session's workflow run.</summary>
	public WorkflowRunner Workflow { get; }

	/// <summary>The workflows visible to this session.</summary>
	internal WorkflowCatalog WorkflowCatalog { get; }

	private WorkflowRunner CreateWorkflowRunner(
		IFileSystem fileSystem, SettingsStore settings, IInferenceService inference, string workspaceRoot) =>
		new(
			WorkflowCatalog,
			new WorkflowRunStore(fileSystem, Core.WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(workspaceRoot))),
			new InferenceWorkflowDecider(inference, workspaceRoot),
			new WorkflowPort(this),
			() => (int)settings.RequireInt(WorkflowSettings.MaxStageAttempts),
			workspaceRoot);

	// Before the runtime starts, a stage's turn is the session's opening input (a TUI's launch turn); afterwards it
	// waits for the agent to finish its current turn.
	private void DeliverWorkflowTurn(WorkflowKickoff kickoff) {
		var turn = new AgentTurnSubmission {
			Id = Guid.NewGuid().ToString("n"),
			Text = kickoff.Text,
			Kind = kickoff.Command.Length > 0 ? AgentTurnSubmissionKind.ProviderCommand : AgentTurnSubmissionKind.Prompt,
			CommandName = kickoff.Command,
			Attachments = [],
		};
		lock (_initialInputGate) {
			if (Volatile.Read(ref _runtimeActivated) == 0 && !_initialInputAssigned) {
				QueueInitialInput(turn);
				_ = Task.Run(() => Workflow.KickoffDelivered(kickoff));
				return;
			}
		}

		(_workflowTurns ??= new IdleTurnDelivery(
			Status,
			new HashSet<SessionStatus> { SessionStatus.Idle, SessionStatus.Waiting },
			SendWorkflowTurn)).Queue(turn);
	}

	private void SendWorkflowTurn(AgentTurnSubmission turn) {
		var kickoff = new WorkflowKickoff { Text = turn.Text, Command = turn.CommandName };
		try {
			SendAgentInput(turn);
		} catch (Exception ex) when (ex is InvalidOperationException or IOException) {
			string reason = $"Weavie couldn't send the stage's turn to the agent: {ex.Message}";
			try {
				Workflow.FailStage(reason);
			} catch (WorkflowException) {
				Notify("error", reason);
			}
			return;
		}

		Workflow.KickoffDelivered(kickoff);
	}

	private void MarkRuntimeActivated() => Volatile.Write(ref _runtimeActivated, 1);

	private void DisposeWorkflow() {
		_workflowTurns?.Dispose();
		Workflow.Dispose();
	}

	private sealed class WorkflowPort(HostSession session) : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn) => session.DeliverWorkflowTurn(turn);

		public void Notify(string message) => session.Notify("warn", message);
	}

	private sealed class WorkflowTurnContext(HostSession session) : IAgentTurnContext {
		public string Compose() => session.Workflow.StageBlock();
	}
}
