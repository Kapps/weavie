using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Weavie.Workflows;

namespace Weavie.Hosting;

// The session's workflow run: Weavie owns its stages and delivers each stage's turn; the agent does the work and
// reports back through the session's registry. See docs/specs/workflows.md.
public sealed partial class HostSession {
	private readonly IdleTurnDelivery<WorkflowKickoff> _workflowTurns;
	private int _runtimeActivated;

	/// <summary>The session's workflow run.</summary>
	public WorkflowRunner Workflow { get; }

	/// <summary>The workflows visible to this session.</summary>
	internal WorkflowCatalog WorkflowCatalog { get; }

	private IdleTurnDelivery<WorkflowKickoff> CreateWorkflowTurns() => new(
		Status,
		new HashSet<SessionStatus> { SessionStatus.Idle, SessionStatus.Waiting },
		SendWorkflowTurn,
		(_, ex) => FailWorkflowTurn(ex));

	private WorkflowRunner CreateWorkflowRunner(
		IFileSystem fileSystem, SettingsStore settings, IWorkflowDecider decider, string workspaceRoot) =>
		new(
			WorkflowCatalog,
			new WorkflowRunStore(fileSystem, Core.WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(workspaceRoot))),
			decider,
			new WorkflowFileReader(fileSystem, workspaceRoot),
			new WorkflowPort(this),
			() => (int)settings.RequireInt(WorkflowSettings.MaxStageAttempts),
			() => settings.RequireBool(WorkflowSettings.OpenReviewItem),
			() => settings.RequireNumber(WorkflowSettings.DecisionConfidence),
			workspaceRoot,
			WeavieWorkflows.Vocabulary);

	// A terminal agent that hasn't launched takes the stage's turn as its launch turn; otherwise the turn waits for
	// the agent to finish its current one. Either way the run must still owe the turn when it is handed over.
	private void DeliverWorkflowTurn(WorkflowKickoff kickoff) {
		lock (_initialInputGate) {
			if (Agent.TerminalSession is not null && Volatile.Read(ref _runtimeActivated) == 0 && !_initialInputAssigned) {
				QueueInitialInput(kickoff.Submission());
				_ = Task.Run(() => Workflow.TryClaimKickoff(kickoff));
				return;
			}
		}

		_workflowTurns.Queue(kickoff);
	}

	private Task SendWorkflowTurn(WorkflowKickoff kickoff) {
		if (Workflow.TryClaimKickoff(kickoff)) {
			SendAgentInput(kickoff.Submission());
		}

		return Task.CompletedTask;
	}

	private void FailWorkflowTurn(Exception ex) {
		string reason = $"Weavie couldn't send the stage's turn to the agent: {ex.Message}";
		try {
			Workflow.FailStage(reason);
		} catch (WorkflowException) {
			Notify("error", reason);
		}
	}

	private void MarkRuntimeActivated() => Volatile.Write(ref _runtimeActivated, 1);

	// The runner closes first, so nothing queues a turn after the delivery is gone.
	private void DisposeWorkflow() {
		Workflow.Dispose();
		_workflowTurns.Dispose();
	}

	private sealed class WorkflowPort(HostSession session) : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn) => session.DeliverWorkflowTurn(turn);

		public void Notify(string message) => session.Notify("warn", message);

		public string? Refusal(WorkflowDefinition definition) => null;
	}

	private sealed class WorkflowTurnContext(HostSession session) : IAgentTurnContext {
		public string Compose() => session.Workflow.StageBlock();
	}
}
