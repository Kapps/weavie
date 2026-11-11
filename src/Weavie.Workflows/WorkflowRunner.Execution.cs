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
		lock (_gate) return _execution is { Stage.Run.Context: WorkflowContext.Continue } ? _execution.Capture() : null;
	}

	internal WorkflowStageCall Capture(WorkflowStageExecution execution) {
		lock (_gate) return new(this, execution, execution.Round);
	}

	internal string StageBlock(WorkflowStageExecution execution) {
		lock (_gate) return ReferenceEquals(_execution, execution) ? WorkflowStageBlock.Compose(_run, _definition, _environment.Words, ownsStage: true) : "";
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

	private WorkflowStageExecution CreateExecution() => new(this, _run!.Id, _definition!.Stages.First(stage => stage.Id == _run.Stage));

	private void UpdateExecution(IReadOnlyList<WorkflowEffect> effects, bool completed) {
		if (_run!.IsFinished || effects.Any(effect => effect is WorkflowEffect.EnterStage)) {
			_execution?.Retire(completed);
			_execution = _run.IsFinished ? null : CreateExecution();
		} else if (effects.Any(effect => effect is WorkflowEffect.ReviseStage)) {
			_execution!.Round++;
		}
	}
}
