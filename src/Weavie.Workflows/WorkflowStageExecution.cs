using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>One visit to a stage, retained across decisions, revisions, and pauses.</summary>
public sealed class WorkflowStageExecution {
	private readonly WorkflowRunner _runner;
	private readonly TaskCompletionSource<bool> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal WorkflowStageExecution(WorkflowRunner runner, string runId, WorkflowStage stage) {
		_runner = runner;
		RunId = runId;
		Stage = stage;
	}

	/// <summary>The run that owns this visit.</summary>
	public string RunId { get; }
	/// <summary>The stage definition for this visit.</summary>
	public WorkflowStage Stage { get; }
	/// <summary>Completes on leaving the visit; true for normal progression, false for cancellation or a jump.</summary>
	public Task<bool> Retired => _retired.Task;
	internal int Round { get; set; }
	internal void Retire(bool completed) => _retired.TrySetResult(completed);

	/// <summary>Captures this visit and its current completion round for one admitted tool call.</summary>
	public WorkflowStageCall Capture() => _runner.Capture(this);
	/// <summary>The stage instructions while this execution remains current.</summary>
	public string Compose() => _runner.StageBlock(this);
	/// <summary>Pauses this visit if its conversation ends while the visit is still current.</summary>
	public void ConversationEnded(string reason) => _runner.ConversationEnded(this, reason);
}

/// <summary>A tool call's exact execution and completion round.</summary>
public sealed class WorkflowStageCall {
	private readonly WorkflowRunner _runner;
	internal WorkflowStageCall(WorkflowRunner runner, WorkflowStageExecution execution, int round) {
		_runner = runner;
		Execution = execution;
		Round = round;
	}
	internal WorkflowStageExecution Execution { get; }
	internal int Round { get; }

	/// <summary>Validates and completes the captured stage visit and round.</summary>
	public string Complete(JsonElement outputs, string summary) => _runner.CompleteStage(this, outputs, summary);
	/// <summary>Pauses the captured stage visit and round.</summary>
	public void Fail(string reason) => _runner.FailStage(this, reason);
}
