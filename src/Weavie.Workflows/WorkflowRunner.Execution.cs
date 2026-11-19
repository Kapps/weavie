using System.Text.Json;

namespace Weavie.Workflows;

public sealed partial class WorkflowRunner {
	private WorkflowStageExecution? _execution;

	/// <summary>The exact current stage visit, including a paused visit, or null once the run ends.</summary>
	public WorkflowStageExecution? Execution {
		get { lock (_gate) return _execution; }
	}

	/// <summary>Captures the current visit and round for one tool admission.</summary>
	public WorkflowStageCall? CaptureStageCall() {
		lock (_gate) return _execution is { Conversation: null } ? _execution.Capture() : null;
	}

	internal WorkflowStageCall Capture(WorkflowStageExecution execution) {
		lock (_gate) return new(this, execution, execution.Round);
	}

	internal string StageBlock(WorkflowStageExecution execution, bool continues) {
		lock (_gate) return ReferenceEquals(_execution, execution)
			? WorkflowStageBlock.Compose(_run, _definition, _environment.Words, ownsStage: true, continues ? execution.Conversation?.Opener : null)
			: "";
	}

	internal string CompleteStage(WorkflowStageCall call, JsonElement outputs, string summary) {
		lock (_gate) {
			Validate(call);
			return CompleteStageLocked(outputs, summary);
		}
	}

	internal void FailStage(WorkflowStageCall call, string reason) {
		lock (_gate) {
			Validate(call);
			Apply(Transitions().Fail(Live(), reason));
		}
	}

	internal void ConversationEnded(WorkflowStageExecution execution, string reason) {
		lock (_gate) {
			if (_closed || !ReferenceEquals(_execution, execution) || _run is not { IsFinished: false } run || run.Phase == WorkflowPhase.Paused) return;
			Apply(Transitions().ConversationEnded(run, reason));
		}
	}

	private void Validate(WorkflowStageCall call) {
		if (!ReferenceEquals(call.Execution, _execution) || call.Round != _execution.Round) {
			throw new WorkflowException("This tool call belongs to a stage execution or completion round that has ended.");
		}
	}

	// A stage with work borrows its turn's conversation; a stage with stages, or a repetition whose entries are done, takes no turn.
	private WorkflowStageExecution CreateExecution() {
		var run = _run!;
		var stage = _definition!.Stage(run.Stage);
		var live = WorkflowTransitions.Live(run);
		bool turns = stage.Run is not null && (!stage.Repeats || live.Stage == stage.Id && live.Entry > 0);
		return new(this, run.Id, stage,
			turns && WorkflowTransitions.TurnConversation(run, _definition) is { } key ? Describe(run, key) : null);
	}

	/// <summary>What the host needs to keep stage conversation <paramref name="key"/> of the live run.</summary>
	public WorkflowStageConversation Conversation(WorkflowConversationRef key) {
		lock (_gate) return Describe(_run ?? throw new WorkflowException("This session has no workflow run."), key);
	}

	/// <summary>Whether conversation <paramref name="key"/> of the live run has taken a turn of the run.</summary>
	public bool HasOpened(WorkflowConversationRef key) {
		ArgumentNullException.ThrowIfNull(key);
		lock (_gate) return _run?.FrameOf(key.Frame)?.Opened.Contains(key.Opener) == true;
	}

	private WorkflowStageConversation Describe(WorkflowRun run, WorkflowConversationRef key) {
		var definition = _definition ?? throw new WorkflowException("This run's definition is unavailable; cancel it.");
		var opener = definition.Stage(key.Opener);
		var owner = run.FrameOf(key.Frame);
		var levels = owner is null ? [] : run.Chain(owner.Id).Reverse().Skip(1).Select(f => f.Entry > 0 ? $"{f.Stage} {f.Entry}" : f.Stage).ToList();
		if (owner is not { Entry: > 0 } || owner.Stage != opener.Id) levels.Add(opener.Id);
		return new WorkflowStageConversation {
			Key = key,
			Opener = opener,
			Source = opener.Context == WorkflowContext.Fork ? WorkflowTransitions.Source(run, key) : null,
			Agent = definition.AgentOf(opener, run.Roles) ?? (opener.Role is { } role
				? throw new WorkflowException($"Run {run.Id} has no agent saved for role \"{role}\"; cancel it and start it again.")
				: null),
			Title = string.Join(" › ", levels),
		};
	}

	private void UpdateExecution(IReadOnlyList<WorkflowEffect> effects, bool completed) {
		if (_run!.IsFinished || effects.Any(effect => effect is WorkflowEffect.EnterStage)) {
			_execution?.Retire(completed);
			_execution = _run.IsFinished ? null : CreateExecution();
		} else if (effects.Any(effect => effect is WorkflowEffect.ReviseStage)) {
			_execution!.Round++;
		}
	}
}
