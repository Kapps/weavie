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
	private readonly Dictionary<string, WorkflowConversationOwner> _workflowConversations = new(StringComparer.Ordinal);

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
		FailWorkflowTurn);

	private WorkflowRunner CreateWorkflowRunner(
		IFileSystem fileSystem, SettingsStore settings, IWorkflowDecider decider, IWorkflowRoleResolver roles, string workspaceRoot,
		Func<string, string> documentsDirectory) =>
		new(
			WorkflowCatalog,
			roles,
			new WorkflowRunStore(fileSystem, Core.WeaviePaths.WorkspaceWorkflowRunFile(WorkspaceId.ForPath(workspaceRoot))),
			decider,
			new WorkflowFileReader(fileSystem),
			new WorkflowPort(this),
			WeavieWorkflows.Environment(settings, workspaceRoot, runId => InheritedWorkflowDocuments ?? documentsDirectory(runId)));

	// A terminal agent that hasn't launched takes the stage's turn as its launch turn; otherwise the turn waits for
	// the agent to finish its current one. Either way the run must still owe the turn when it is handed over.
	private void DeliverWorkflowTurn(OwedWorkflowTurn owed) {
		var kickoff = owed.Kickoff;
		if (kickoff.Context != WorkflowContext.Continue && Workflow.Execution is { Opener: { } opener } execution) {
			var conversation = ConversationOwner(execution.RunId, opener);
			_ = Background.Run(ct => conversation.SendAsync(execution, owed, ct));
			return;
		}
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
		if (owed.Refusal is { } refusal) {
			Workflow.RefuseOpen(owed.Kickoff, refusal);
			return;
		}
		if (Workflow.TryClaimKickoff(owed.Kickoff)) {
			SendAgentInput(owed.Submission());
		}
	}

	private void FailWorkflowTurn(OwedWorkflowTurn owed, Exception ex) {
		string reason = $"Weavie couldn't send the stage's turn to the agent: {ex.Message}";
		try {
			Workflow.FailTurn(owed.Kickoff, reason);
		} catch (WorkflowException) {
			Notify("error", reason);
		}
	}

	private WorkflowConversationOwner ConversationOwner(string runId, WorkflowStage opener) {
		lock (_workflowConversations) {
			if (_workflowConversations.TryGetValue(opener.Id, out var owner)) return owner;
			if (_workflowConversations.Count == 0) ForgetWorkflowConversations(key => key.Scope != runId);
			owner = new WorkflowConversationOwner(this, runId, opener, Workflow.AgentOf(opener.Id));
			_workflowConversations.Add(opener.Id, owner);
			_ = Background.Run(owner.RunLifetimeAsync);
			return owner;
		}
	}

	// The run ended or was replaced: its conversations close, and any it kept that aren't live are forgotten.
	private void WireWorkflowConversations() => Workflow.Changed += () => {
		string? live = Workflow.Current is { IsFinished: false } run ? run.Id : null;
		lock (_workflowConversations) {
			if (live is not null && _workflowConversations.Values.All(owner => owner.Key.Scope == live)) return;
			EndWorkflowConversations();
		}
		ForgetWorkflowConversations(key => key.Scope != live);
	};

	private void EndWorkflowConversations() {
		bool completed = Workflow.Current?.Phase == WorkflowPhase.Done;
		foreach (var owner in _workflowConversations.Values) owner.End(completed);
		_workflowConversations.Clear();
	}

	// Runner callbacks hold its lock, which comes after the agent's; forgetting takes the agent's, so it runs apart.
	private void ForgetWorkflowConversations(Func<AgentConversationKey, bool> owners) => _ = Background.Run(async _ => {
		await Task.Yield();
		if (Agent.Session is IStructuredAgentConversations conversations) conversations.Forget(owners);
	});

	private void MarkRuntimeActivated() => Volatile.Write(ref _runtimeActivated, 1);

	// The runner closes first, so nothing queues a turn or a release after the deliveries are gone.
	private void DisposeWorkflow() {
		try {
			// The runner's lock comes before the owners', so the visit is read before the owners are.
			var execution = Workflow.Execution;
			bool lent;
			lock (_workflowConversations) lent = execution is { Opener: { } opener }
				&& _workflowConversations.GetValueOrDefault(opener.Id)?.Lends(execution) == true;
			if (lent && Workflow.Current?.Kickoff is null) {
				execution!.ConversationEnded("The stage conversation ended when the session unloaded. Resume to continue.");
			}
		} finally {
			Workflow.Dispose();
			_workflowTurns.Dispose();
			_workflowRelease.Dispose();
		}
	}

	// A turn the run owes, with the prompt images it carries.
	private sealed record OwedWorkflowTurn(WorkflowKickoff Kickoff, IReadOnlyList<WorkflowTurnImage> Images) {
		public AgentTurnSubmission Submission() => Kickoff.Submission(Images);
		public string? Refusal => Images.FirstOrDefault(image => !File.Exists(image.Path)) is { } missing
			? $"The prompt's image {missing.Path} is missing, so the stage's turn was not sent." : null;
	}

	private sealed class WorkflowPort(HostSession session) : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) => session.DeliverWorkflowTurn(new(turn, images));

		public void Notify(string message) => session.Notify("warn", message);

		public string? Refusal(WorkflowLaunch launch) => WorkflowAgentControls.Refusal(
			launch.Definition, launch.Roles, session.Agent.Provider, session.Agent.Controls?.ControlState is { Ready: true } state ? state.Axes : null);
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
