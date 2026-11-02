using System.Security.Cryptography;
using System.Text.Json;

namespace Weavie.Core.Workflows;

/// <summary>What a session's runner needs from its owner: deliver turns and tell the user.</summary>
public interface IWorkflowRunPort {
	/// <summary>Sends <paramref name="text"/> to the session's agent as its next turn, once it can take one.</summary>
	void Kickoff(string text);

	/// <summary>Shows <paramref name="message"/> to the user.</summary>
	void Notify(string message);
}

/// <summary>
/// A session's workflow run: applies transitions under one lock, persists each step, and performs its effects.
/// A decision in flight is an owned <see cref="PendingDecision"/>; its answer lands only while it is still current,
/// so an answer arriving after a cancel, jump, or newer decision is dropped by construction.
/// </summary>
public sealed class WorkflowRunner : IDisposable {
	private readonly Lock _gate = new();
	private readonly WorkflowCatalog _catalog;
	private readonly WorkflowRunStore _store;
	private readonly IWorkflowDecider _decider;
	private readonly IWorkflowRunPort _port;
	private readonly Func<int> _maxStageAttempts;
	private readonly string _worktree;
	private WorkflowRun? _run;
	private WorkflowDefinition? _definition;
	private PendingDecision? _pending;

	/// <summary>Creates the runner over the session's persisted run; call <see cref="Activate"/> once the port is live.</summary>
	public WorkflowRunner(
		WorkflowCatalog catalog, WorkflowRunStore store, IWorkflowDecider decider, IWorkflowRunPort port,
		Func<int> maxStageAttempts, string worktree) {
		_catalog = catalog;
		_store = store;
		_decider = decider;
		_port = port;
		_maxStageAttempts = maxStageAttempts;
		_worktree = worktree;
		_run = store.Run;
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

	/// <summary>Resumes the persisted run: re-reads its definition and re-performs any owed turn or decision.</summary>
	public void Activate() {
		if (_store.LoadFailure is { } failure) {
			_port.Notify(failure);
		}

		lock (_gate) {
			if (_run is not { IsFinished: false } run) {
				return;
			}

			var read = WorkflowReader.Read(run.SourceText, run.WorkflowId);
			if (read.Definition is null) {
				Commit(run with { Phase = WorkflowPhase.Paused, Kickoff = null, PausedReason = $"Its definition no longer validates: {string.Join("; ", read.Diagnostics)}. Cancel it and start again." }, []);
				_port.Notify($"Workflow {run.WorkflowId} paused: {run.WorkflowId}'s saved definition no longer validates.");
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
			return WorkflowStageBlock.Compose(_run, _definition);
		}
	}

	/// <summary>Starts <paramref name="workflowId"/> with <paramref name="inputs"/>; refuses while another run is live.</summary>
	public WorkflowRun Start(string workflowId, JsonElement inputs) {
		lock (_gate) {
			if (_run is { IsFinished: false } live) {
				throw new WorkflowException($"Run {live.Id} of {live.WorkflowId} is still live; cancel it first.");
			}

			var definition = _catalog.Resolve(workflowId);
			var values = WorkflowTransitions.ReadInputs(definition, inputs, _worktree);
			string path = _catalog.List().First(e => e.Id == workflowId && e.ShadowedBy is null).Path;
			_definition = definition;
			Apply(Transitions().Start($"r-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}", path, values));
			return _run!;
		}
	}

	/// <summary>The agent completes the current stage; returns the reply for the agent.</summary>
	public string CompleteStage(JsonElement outputs) {
		lock (_gate) {
			var completion = Transitions().Complete(Live(), outputs);
			Apply(completion.Step);
			if (completion.Errors.Count > 0) {
				return _run!.Phase == WorkflowPhase.Paused
					? $"Rejected again, so the run is paused for the user: {_run.PausedReason}"
					: $"Not complete — fix these and call completeStage again:\n{string.Join("\n", completion.Errors.Select(e => $"- {e}"))}";
			}

			return _run!.Phase switch {
				WorkflowPhase.AwaitingApproval => "Complete. The stage now waits for the user's approval; summarize what to review and end your turn.",
				WorkflowPhase.Done => "Complete. The workflow is finished.",
				_ => "Complete. The next stage starts after this turn — end your turn now.",
			};
		}
	}

	/// <summary>The agent can't finish the current stage.</summary>
	public void FailStage(string reason) => Mutate(null, run => Transitions().Fail(run, reason));

	/// <summary>The user approves run <paramref name="runId"/>'s stage.</summary>
	public void Approve(string runId) => Mutate(runId, run => Transitions().Approve(run));

	/// <summary>The user rejects run <paramref name="runId"/>'s stage with <paramref name="feedback"/>.</summary>
	public void Reject(string runId, string feedback) => Mutate(runId, run => Transitions().Reject(run, feedback));

	/// <summary>The user resumes paused run <paramref name="runId"/>.</summary>
	public void Resume(string runId) => Mutate(runId, run => Transitions().Resume(run));

	/// <summary>The user moves run <paramref name="runId"/> to <paramref name="stage"/>.</summary>
	public void Jump(string runId, string stage) => Mutate(runId, run => Transitions().Jump(run, stage));

	/// <summary>The user cancels run <paramref name="runId"/>.</summary>
	public void Cancel(string runId) => Mutate(runId, run => Transitions().Cancel(run));

	/// <summary>Records that the owed kickoff turn was handed to the agent.</summary>
	public void KickoffDelivered(string text) {
		lock (_gate) {
			if (_run?.Kickoff == text) {
				Commit(_run with { Kickoff = null }, []);
			}
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
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
		_run is { IsFinished: false } run ? run : throw new WorkflowException("No workflow run is live in this session.");

	private WorkflowTransitions Transitions() =>
		new(_definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it."), _maxStageAttempts(), _worktree);

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
				case WorkflowEffect.Kickoff kickoff: _port.Kickoff(kickoff.Text); break;
				case WorkflowEffect.Notify notify: _port.Notify(notify.Message); break;
				case WorkflowEffect.Decide decide: _pending = new PendingDecision(this, decide.Request); break;
			}
		}

		Changed?.Invoke();
	}

	private void Resolve(PendingDecision decision, WorkflowDecisionResult result) {
		lock (_gate) {
			if (_pending != decision) {
				return;
			}

			Apply(Transitions().Resolve(_run!, result));
		}
	}

	private sealed class PendingDecision : IDisposable {
		private readonly CancellationTokenSource _cts = new();

		public PendingDecision(WorkflowRunner owner, WorkflowDecisionRequest request) {
			_ = Task.Run(async () => {
				WorkflowDecisionResult result;
				try {
					result = await owner._decider.DecideAsync(request, _cts.Token).ConfigureAwait(false);
				} catch (OperationCanceledException) {
					return;
				} catch (Exception ex) {
					result = WorkflowDecisionResult.Failed(ex.Message);
				}

				owner.Resolve(this, result);
			});
		}

		public void Dispose() {
			_cts.Cancel();
			_cts.Dispose();
		}
	}
}
