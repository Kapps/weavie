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
	private readonly IdleTurnDelivery<OwedWorkflowTurn> _workflowTurns;
	private int _runtimeActivated;

	internal string? InheritedWorkflowDocuments { get; set; }

	internal string? WorkflowDocumentsDirectory => Workflow.Current?.DocumentsDirectory ?? InheritedWorkflowDocuments;

	/// <summary>The session's workflow run.</summary>
	public WorkflowRunner Workflow { get; }

	/// <summary>The workflows visible to this session.</summary>
	internal WorkflowCatalog WorkflowCatalog { get; }

	private IdleTurnDelivery<OwedWorkflowTurn> CreateWorkflowTurns() => new(
		Status,
		new HashSet<SessionStatus> { SessionStatus.Idle, SessionStatus.Waiting },
		SendWorkflowTurn,
		(_, ex) => FailWorkflowTurn(ex));

	private WorkflowRunner CreateWorkflowRunner(
		IFileSystem fileSystem, SettingsStore settings, IWorkflowDecider decider, string workspaceRoot, Func<string, string> documentsDirectory) =>
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
			WeavieWorkflows.Vocabulary,
			runId => InheritedWorkflowDocuments ?? documentsDirectory(runId));

	// A terminal agent that hasn't launched takes the stage's turn as its launch turn; otherwise the turn waits for
	// the agent to finish its current one. Either way the run must still owe the turn when it is handed over.
	private void DeliverWorkflowTurn(OwedWorkflowTurn owed) {
		lock (_initialInputGate) {
			if (Agent.TerminalSession is not null && Volatile.Read(ref _runtimeActivated) == 0 && !_initialInputAssigned) {
				QueueInitialInput(owed.Submission());
				_ = Task.Run(() => Workflow.TryClaimKickoff(owed.Kickoff));
				return;
			}
		}

		_workflowTurns.Queue(owed);
	}

	private async Task SendWorkflowTurn(OwedWorkflowTurn owed) {
		if (owed.Kickoff.Opens.Length > 0 && !await OpenStageAsync(owed.Kickoff).ConfigureAwait(false)) return;
		if (Workflow.TryClaimKickoff(owed.Kickoff)) {
			SendAgentInput(owed.Submission());
		}
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

	// The runner closes first, so nothing queues a turn or a release after the deliveries are gone.
	private void DisposeWorkflow() {
		Workflow.Dispose();
		_workflowTurns.Dispose();
		_workflowRelease.Dispose();
	}

	// A turn the run owes, with the prompt images it carries.
	private sealed record OwedWorkflowTurn(WorkflowKickoff Kickoff, IReadOnlyList<WorkflowTurnImage> Images) {
		public AgentTurnSubmission Submission() => Kickoff.Submission(Images);
	}

	private sealed class WorkflowPort(HostSession session) : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) => session.DeliverWorkflowTurn(new(turn, images));

		public void Notify(string message) => session.Notify("warn", message);

		public string? Refusal(WorkflowDefinition definition) => WorkflowAgentControls.Refusal(
			definition, session.Agent.Provider, session.Agent.Controls?.ControlState is { Ready: true } state ? state.Axes : null);
	}

	private sealed class WorkflowTurnContext(HostSession session) : IAgentTurnContext {
		public string Compose() {
			string stage = session.Workflow.StageBlock();
			return stage.Length > 0 || session.WorkflowDocumentsDirectory is not { } documents
				? stage
				: $"[Weavie workflow documents] Shared documents: {documents}. Read and update files here for the workflow that started this session. Sessions started from here share this same folder.";
		}
	}
}
