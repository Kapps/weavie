using System.Security.Cryptography;
using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>What a session's runner needs from its owner: deliver turns and tell the user.</summary>
public interface IWorkflowRunPort {
	/// <summary>Sends <paramref name="turn"/> to the session's agent as its next turn, once it can take one.</summary>
	void Kickoff(WorkflowKickoff turn);

	/// <summary>Shows <paramref name="message"/> to the user.</summary>
	void Notify(string message);

	/// <summary>Why this session can't run <paramref name="definition"/>, or <see langword="null"/>; called outside the runner's lock.</summary>
	string? Refusal(WorkflowDefinition definition);
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
	private readonly Func<int> _maxStageAttempts;
	private readonly Func<bool> _openReviewItem;
	private readonly Func<double> _decisionConfidence;
	private readonly string _worktree;
	private readonly WorkflowVocabulary _words;
	private WorkflowRun? _run;
	private WorkflowDefinition? _definition;
	private PendingDecision? _pending;
	private bool _closed;

	/// <summary>Creates the runner over the session's persisted run; call <see cref="Activate"/> once the port is live.</summary>
	public WorkflowRunner(
		WorkflowCatalog catalog, IWorkflowRunStore store, IWorkflowDecider decider, IWorkflowFileReader files, IWorkflowRunPort port,
		Func<int> maxStageAttempts, Func<bool> openReviewItem, Func<double> decisionConfidence, string worktree, WorkflowVocabulary words) {
		_catalog = catalog;
		_store = store;
		_decider = decider;
		_files = files;
		_port = port;
		_maxStageAttempts = maxStageAttempts;
		_openReviewItem = openReviewItem;
		_decisionConfidence = decisionConfidence;
		_worktree = worktree;
		_words = words;
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
			if (_run is not { IsFinished: false } run) {
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
			if (run.Kickoff is { } kickoff) _port.Kickoff(kickoff);
			if (run.Phase == WorkflowPhase.Deciding) {
				Apply(Transitions().Resume(run with { Phase = WorkflowPhase.Paused }));
			}
		}
	}

	/// <summary>The stage block for the agent's next turn, or empty.</summary>
	public string StageBlock() {
		lock (_gate) {
			return WorkflowStageBlock.Compose(_run, _definition, _words);
		}
	}

	/// <summary>Starts <paramref name="workflowId"/> with <paramref name="inputs"/>; refuses while another run is live.</summary>
	public WorkflowRun Start(string workflowId, JsonElement inputs) => Start(_catalog.Prepare(workflowId, inputs));

	/// <summary>Starts the resolved <paramref name="launch"/>; refuses while another run is live.</summary>
	public WorkflowRun Start(WorkflowLaunch launch) {
		if (_port.Refusal(launch.Definition) is { } refusal) {
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
			Apply(Transitions().Start(
				$"r-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}", launch.SourcePath, launch.Inputs, _run?.Hold));
			return _run!;
		}
	}

	/// <summary>The agent completes the current stage with its outputs and a summary; returns the reply for the agent.</summary>
	public string CompleteStage(JsonElement outputs, string summary) {
		lock (_gate) {
			var completion = Transitions().Complete(Live(), outputs, summary);
			Apply(completion.Step);
			if (completion.Errors.Count > 0) {
				return _run!.Phase == WorkflowPhase.Paused
					? $"Rejected again, so the run is paused for the user: {_run.PausedReason}"
					: $"Not complete — fix these and call {_words.CompleteStage} again:\n{string.Join("\n", completion.Errors.Select(e => $"- {e}"))}";
			}

			return _run!.Phase switch {
				WorkflowPhase.Asking => $"Complete. {WorkflowStageBlock.PutToUser(_run.Asked!, _words, _openReviewItem())}",
				WorkflowPhase.Done => "Complete. The workflow is finished.",
				_ => "Complete. The next stage starts after this turn — end your turn now.",
			};
		}
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public void FailStage(string reason) => Mutate(null, run => Transitions().Fail(run, reason));

	/// <summary>The user answers run <paramref name="runId"/>'s question with <paramref name="choice"/> and <paramref name="feedback"/> (empty for none).</summary>
	public void Answer(string runId, string choice, string feedback) => Mutate(runId, run => Transitions().Answer(run, choice, feedback));

	/// <summary>The user resumes paused run <paramref name="runId"/>.</summary>
	public void Resume(string runId) => Mutate(runId, run => Transitions().Resume(run));

	/// <summary>The user moves run <paramref name="runId"/> to <paramref name="stage"/>.</summary>
	public void Jump(string runId, string stage) => Mutate(runId, run => Transitions().Jump(run, stage));

	/// <summary>The user cancels run <paramref name="runId"/>.</summary>
	public void Cancel(string runId) => Mutate(runId, run => Transitions().Cancel(run));

	/// <summary>The agent that runs stage <paramref name="stage"/> of the run's definition, or <see langword="null"/> for the session's own.</summary>
	public WorkflowAgent? AgentOf(string stage) {
		lock (_gate) {
			var definition = _definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it.");
			return definition.AgentOf(definition.Stages.First(s => s.Id == stage));
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

			Commit(_run! with { Kickoff = null }, []);
			return true;
		}
	}

	/// <summary>Closes the runner: the pending decision is dropped and every later change is refused.</summary>
	public void Dispose() {
		lock (_gate) {
			_closed = true;
			_pending?.Dispose();
			_pending = null;
		}
	}

	private void Mutate(string? runId, Func<WorkflowRun, WorkflowStep> transition) {
		lock (_gate) {
			var run = Live();
			if (runId is not null && run.Id != runId) {
				throw new WorkflowException($"Run {runId} isn't this session's live run ({run.Id}).");
			}

			Apply(transition(run));
		}
	}

	private WorkflowRun Live() =>
		_closed ? throw new WorkflowException("This session is closing; its workflow run can't change.")
		: _run is { IsFinished: false } run ? run : throw new WorkflowException("No workflow run is live in this session.");

	private WorkflowTransitions Transitions() =>
		new(_definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it."), _maxStageAttempts(), _openReviewItem(), _decisionConfidence(), _worktree, _words);

	private void Apply(WorkflowStep step) {
		_pending?.Dispose();
		_pending = null;
		Commit(step.Run, step.Effects);
	}

	private void Commit(WorkflowRun run, IReadOnlyList<WorkflowEffect> effects) {
		_run = run;
		_store.Save(run);
		foreach (var effect in effects) {
			switch (effect) {
				case WorkflowEffect.Kickoff kickoff: _port.Kickoff(kickoff.Turn); break;
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

			Apply(Transitions().Resolve(_run!, result, decision.MinimumConfidence));
		}
	}

}
