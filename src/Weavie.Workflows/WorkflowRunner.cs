using System.Security.Cryptography;
using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>What a session's runner needs from its owner: deliver turns and tell the user.</summary>
public interface IWorkflowRunPort {
	/// <summary>Sends <paramref name="turn"/> with <paramref name="images"/> to the session's agent as its next turn, once it can take one.</summary>
	void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images);

	/// <summary>Shows <paramref name="message"/> to the user.</summary>
	void Notify(string message);

	/// <summary>Why this session can't run <paramref name="launch"/>, or <see langword="null"/>; called outside the runner's lock.</summary>
	string? Refusal(WorkflowLaunch launch);
}

/// <summary>
/// A session's workflow run: applies transitions under one lock, persists each step, and performs its effects.
/// A decision in flight is an owned <see cref="PendingDecision"/>; its answer lands only while it is still current,
/// so an answer arriving after a cancel, jump, or newer decision is dropped by construction.
/// </summary>
public sealed partial class WorkflowRunner : IDisposable {
	private readonly Lock _gate = new();
	private readonly WorkflowCatalog _catalog;
	private readonly IWorkflowRunStore _store;
	private readonly IWorkflowDecider _decider;
	private readonly IWorkflowFileReader _files;
	private readonly IWorkflowRunPort _port;
	private readonly IWorkflowRoleResolver _roles;
	private readonly WorkflowEnvironment _environment;
	private WorkflowRun? _run;
	private WorkflowDefinition? _definition;
	private PendingDecision? _pending;
	private bool _closed;

	/// <summary>Creates the runner over the session's persisted run; call <see cref="Activate"/> once the port is live.</summary>
	public WorkflowRunner(
		WorkflowCatalog catalog, IWorkflowRoleResolver roles, IWorkflowRunStore store, IWorkflowDecider decider, IWorkflowFileReader files, IWorkflowRunPort port,
		WorkflowEnvironment environment) {
		_catalog = catalog;
		_roles = roles;
		_store = store;
		_decider = decider;
		_files = files;
		_port = port;
		_environment = environment;
		_run = store.Run;
		Problem = store.LoadFailure;
	}

	/// <summary>Raised after every change to the run.</summary>
	public event Action? Changed;

	/// <summary>The current run, including a finished one, or <see langword="null"/>.</summary>
	public WorkflowRun? Current {
		get {
			lock (_gate) {
				return _run;
			}
		}
	}

	/// <summary>Why the session's saved run couldn't be loaded, until a new run starts; otherwise <see langword="null"/>.</summary>
	public string? Problem { get; private set; }

	/// <summary>Resumes the persisted run: re-reads its definition and re-performs any owed turn or decision.</summary>
	public void Activate() {
		lock (_gate) {
			if (_run is not { } run) {
				return;
			}
			if (run.IsFinished) {
				if (run.Kickoff is { } report) Deliver(run, report);
				return;
			}

			var read = WorkflowReader.Read(run.SourceText, run.WorkflowId);
			if (read.Definition is null) {
				Commit(run with {
					Phase = WorkflowPhase.Paused,
					Kickoff = null,
					PausedReason = $"The saved definition no longer validates ({string.Join("; ", read.Diagnostics)}); cancel the run and start it again.",
				}, []);
				return;
			}

			_definition = read.Definition;
			_execution = CreateExecution();
			if (_execution.Stage.Run.Context != WorkflowContext.Continue && run.Phase != WorkflowPhase.Paused
				&& run.Decision != WorkflowDecisionKind.If
				&& run.Kickoff is not { Context: not WorkflowContext.Continue }) {
				_execution.ConversationEnded("The stage conversation ended when the session unloaded or restarted. Resume to continue.");
				return;
			}
			if (run.Kickoff is { } kickoff) Deliver(run, kickoff);
			if (run.Phase == WorkflowPhase.Deciding) {
				Apply(Transitions().Resume(run with { Phase = WorkflowPhase.Paused }));
			}
		}
	}

	/// <summary>The stage block for the agent's next turn, or empty.</summary>
	public string StageBlock() {
		lock (_gate) {
			return WorkflowStageBlock.Compose(_run, _definition, _environment.Words, ownsStage: false, continued: null);
		}
	}

	/// <summary>Prepares <paramref name="workflowId"/> for this session, filling its roles for the session's provider.</summary>
	public WorkflowLaunch Prepare(string workflowId, JsonElement inputs, string? documentsDirectory, WorkflowStartPrompt prompt) =>
		_catalog.Prepare(workflowId, inputs, documentsDirectory, prompt, _roles);

	/// <summary>Starts <paramref name="workflowId"/> with <paramref name="inputs"/>; refuses while another run is live.</summary>
	public WorkflowRun Start(string workflowId, JsonElement inputs) => Start(Prepare(workflowId, inputs, null, WorkflowStartPrompt.None));

	/// <summary>Starts the resolved <paramref name="launch"/>; refuses while another run is live.</summary>
	public WorkflowRun Start(WorkflowLaunch launch) {
		ArgumentNullException.ThrowIfNull(launch);
		if (_port.Refusal(launch) is { } refusal) {
			throw new WorkflowException(refusal);
		}

		lock (_gate) {
			if (_closed) {
				throw new WorkflowException("This session is closing; it can't start a workflow run.");
			}

			if (_run is { IsFinished: false } live) {
				throw new WorkflowException($"Run {live.Id} of {live.WorkflowId} is still live; cancel it first.");
			}

			_definition = launch.Definition;
			Problem = null;
			string id = $"r-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";
			string documents = _environment.DocumentsDirectory(id);
			try {
				Directory.CreateDirectory(documents);
			} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
				throw new WorkflowException($"Could not create workflow documents directory {documents}: {ex.Message}");
			}
			Apply(Transitions().Start(new WorkflowRunStart {
				RunId = id,
				SourcePath = launch.SourcePath,
				Inputs = launch.Inputs,
				DocumentsDirectory = documents,
				Hold = _run?.Hold,
				Prompt = SavePrompt(launch.Prompt, documents, id),
				Roles = launch.Roles,
			}));
			return _run!;
		}
	}

	// A run's images sit under its own id, so a run sharing an inherited folder never meets another's.
	private static WorkflowPrompt SavePrompt(WorkflowStartPrompt prompt, string documents, string runId) {
		string folder = Path.Combine("prompt", runId);
		try {
			if (prompt.Images.Count > 0) Directory.CreateDirectory(Path.Combine(documents, folder));
			return new WorkflowPrompt {
				Text = prompt.Text,
				Images = [.. prompt.Images.Select((image, i) => {
					string file = Path.Combine(folder, $"image-{i + 1}{image.Extension}");
					File.WriteAllBytes(Path.Combine(documents, file), image.Bytes);
					return new WorkflowPromptImage { File = file, Mime = image.Mime };
				})],
			};
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			throw new WorkflowException($"Could not save the prompt's images under {Path.Combine(documents, folder)}: {ex.Message}");
		}
	}

	private void Deliver(WorkflowRun run, WorkflowKickoff turn) => _port.Kickoff(turn, turn.CarriesPromptImages
		? [.. run.Prompt.Images.Select(image => new WorkflowTurnImage(Path.Combine(run.DocumentsDirectory, image.File), image.Mime))]
		: []);

	/// <summary>The agent completes the current stage with its outputs and a summary; returns the reply for the agent.</summary>
	public string CompleteStage(JsonElement outputs, string summary) {
		var call = CaptureStageCall() ?? throw new WorkflowException("This conversation does not own an open workflow stage.");
		return call.Complete(outputs, summary);
	}

	private string CompleteStageLocked(JsonElement outputs, string summary) {
		lock (_gate) {
			bool independent = _execution!.Stage.Run.Context != WorkflowContext.Continue;
			var completion = Transitions().Complete(Live(), outputs, summary);
			Commit(completion.Step.Run, completion.Step.Effects, replaceDecision: true, completed: true);
			if (completion.Errors.Count > 0) {
				return _run!.Phase == WorkflowPhase.Paused
					? $"Rejected again, so the run is paused for the user: {_run.PausedReason}"
					: $"Not complete — fix these and call {_environment.Words.CompleteStage} again:\n{string.Join("\n", completion.Errors.Select(e => $"- {e}"))}";
			}
			if (independent) return "Complete. The main conversation receives the result and any user question. End your turn now.";

			return _run!.Phase switch {
				WorkflowPhase.Asking => $"Complete. {WorkflowStageBlock.PutToUser(_run.Asked!, _environment)}",
				WorkflowPhase.Done => "Complete. The workflow is finished.",
				_ => "Complete. The next stage starts after this turn — end your turn now.",
			};
		}
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public void FailStage(string reason) => Mutate(null, run => Transitions().Fail(run, reason));

	/// <summary>
	/// The claimed <paramref name="turn"/> never reached the agent, so the stage fails with <paramref name="reason"/>
	/// and the prompt images it carried are still owed to the agent.
	/// </summary>
	public void FailTurn(WorkflowKickoff turn, string reason) {
		ArgumentNullException.ThrowIfNull(turn);
		Mutate(null, run => Transitions().Fail(turn.Context == WorkflowContext.Continue && turn.CarriesPromptImages ? run with { PromptImagesSent = false } : run, reason));
	}

	/// <summary>
	/// The stage <paramref name="turn"/> opens couldn't open, so the turn is never sent and the run pauses with
	/// <paramref name="reason"/>; nothing changes once the run no longer owes that turn.
	/// </summary>
	public void RefuseOpen(WorkflowKickoff turn, string reason) {
		lock (_gate) {
			if (Owes(turn)) Apply(Transitions().Halt(_run!, reason));
		}
	}

	/// <summary>The user answers run <paramref name="runId"/>'s question with <paramref name="choice"/> and <paramref name="feedback"/> (empty for none).</summary>
	public void Answer(string runId, string choice, string feedback) => Mutate(runId, run => Transitions().Answer(run, choice, feedback), completed: true);

	/// <summary>The user resumes paused run <paramref name="runId"/>.</summary>
	public void Resume(string runId) => Mutate(runId, run => Transitions().Resume(run));

	/// <summary>The user moves run <paramref name="runId"/> to <paramref name="stage"/>.</summary>
	public void Jump(string runId, string stage) => Mutate(runId, run => Transitions().Jump(run, stage));

	/// <summary>The user cancels run <paramref name="runId"/>.</summary>
	public void Cancel(string runId) => Mutate(runId, run => Transitions().Cancel(run));

	/// <summary>
	/// The agent that runs stage <paramref name="stage"/> of the run's definition: its pinned agent or its role's, or
	/// <see langword="null"/> for the session's own. A role never runs on the session's own model.
	/// </summary>
	public WorkflowAgent? AgentOf(string stage) {
		lock (_gate) {
			var definition = _definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it.");
			var run = _run ?? throw new WorkflowException("This session has no workflow run.");
			var open = definition.Stages.First(s => s.Id == stage);
			return definition.AgentOf(open, run.Roles) ?? (open.Run.Role is { } role
				? throw new WorkflowException($"Run {run.Id} has no agent saved for role \"{role}\"; cancel it and start it again.")
				: null);
		}
	}

	/// <summary>Whether the run still owes exactly <paramref name="turn"/>.</summary>
	public bool Owes(WorkflowKickoff turn) {
		lock (_gate) {
			return !_closed && _run?.Kickoff == turn;
		}
	}

	/// <summary>
	/// Records the control values run <paramref name="runId"/> holds, live or finished; false when it is no longer the
	/// session's run, whose successor then owns the hold.
	/// </summary>
	public bool SetHold(string runId, WorkflowHold? hold) {
		lock (_gate) {
			if (_closed || _run?.Id != runId) {
				return false;
			}

			Commit(_run with { Hold = hold }, []);
			return true;
		}
	}

	/// <summary>
	/// Claims <paramref name="turn"/> for sending: true only while the run still owes exactly that turn, which it then
	/// stops owing. A turn the run no longer owes (cancelled, paused, replaced, or the session closing) is never sent.
	/// </summary>
	public bool TryClaimKickoff(WorkflowKickoff turn) {
		lock (_gate) {
			if (!Owes(turn)) {
				return false;
			}

			Commit(_run! with { Kickoff = null, PromptImagesSent = _run.PromptImagesSent || turn.Context == WorkflowContext.Continue && turn.CarriesPromptImages, Unreported = turn.Context == WorkflowContext.Continue ? "" : _run.Unreported }, []);
			return true;
		}
	}

	/// <summary>Closes the runner: the pending decision is dropped and every later change is refused.</summary>
	public void Dispose() {
		lock (_gate) {
			_closed = true;
			_execution?.Retire(completed: false);
			_execution = null;
			_pending?.Dispose();
			_pending = null;
		}
	}

	private void Mutate(string? runId, Func<WorkflowRun, WorkflowStep> transition) => Mutate(runId, transition, completed: false);

	private void Mutate(string? runId, Func<WorkflowRun, WorkflowStep> transition, bool completed) {
		lock (_gate) {
			var run = Live();
			if (runId is not null && run.Id != runId) {
				throw new WorkflowException($"Run {runId} isn't this session's live run ({run.Id}).");
			}

			var step = transition(run);
			Commit(step.Run, step.Effects, replaceDecision: true, completed);
		}
	}

	private WorkflowRun Live() =>
		_closed ? throw new WorkflowException("This session is closing; its workflow run can't change.")
		: _run is { IsFinished: false } run ? run : throw new WorkflowException("No workflow run is live in this session.");

	private WorkflowTransitions Transitions() =>
		new(_definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it."), _environment);

	private void Apply(WorkflowStep step) => Commit(step.Run, step.Effects, replaceDecision: true, completed: false);

	private void Commit(WorkflowRun run, IReadOnlyList<WorkflowEffect> effects) => Commit(run, effects, replaceDecision: false, completed: false);

	private void Commit(WorkflowRun run, IReadOnlyList<WorkflowEffect> effects, bool replaceDecision, bool completed) {
		_store.Save(run);
		if (replaceDecision) {
			_pending?.Dispose();
			_pending = null;
		}
		_run = run;
		UpdateExecution(effects, completed);
		foreach (var effect in effects) {
			switch (effect) {
				case WorkflowEffect.Kickoff kickoff: Deliver(run, kickoff.Turn); break;
				case WorkflowEffect.Notify notify: _port.Notify(notify.Message); break;
				case WorkflowEffect.Decide decide: _pending = new PendingDecision(decide.Plan, _files, _decider, Resolve); break;
			}
		}

		Changed?.Invoke();
	}

	private void Resolve(PendingDecision decision, WorkflowDecisionResult result) {
		lock (_gate) {
			if (_pending != decision) {
				return;
			}

			try {
				var step = Transitions().Resolve(_run!, result, decision.MinimumConfidence);
				Commit(step.Run, step.Effects, replaceDecision: true, completed: true);
			} catch (Exception ex) when (ex is WorkflowException or IOException or UnauthorizedAccessException) {
				_pending?.Dispose();
				_pending = null;
				string reason = $"Could not save workflow progress: {ex.Message}. The run is paused in memory; fix storage and resume. The saved run still holds the pending decision.";
				_run = _run! with { Phase = WorkflowPhase.Paused, PausedReason = reason, Kickoff = null };
				_port.Notify(reason);
				Changed?.Invoke();
			}
		}
	}

}
