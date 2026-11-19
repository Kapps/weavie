using System.Text.Json;

namespace Weavie.Workflows;

/// <summary>One visit to a stage, retained across decisions, revisions, and pauses.</summary>
public sealed class WorkflowStageExecution {
	private readonly WorkflowRunner _runner;
	private readonly TaskCompletionSource<bool> _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal WorkflowStageExecution(WorkflowRunner runner, string runId, WorkflowStage stage, WorkflowStageConversation? conversation) {
		_runner = runner;
		RunId = runId;
		Stage = stage;
		Conversation = conversation;
	}

	/// <summary>The run that owns this visit.</summary>
	public string RunId { get; }
	/// <summary>The stage definition for this visit.</summary>
	public WorkflowStage Stage { get; }
	/// <summary>The stage conversation this visit's turns run in, or null for main or a stage that takes no turn.</summary>
	public WorkflowStageConversation? Conversation { get; }
	/// <summary>Completes on leaving the visit; true for normal progression, false for cancellation or a jump.</summary>
	public Task<bool> Retired => _retired.Task;
	internal int Round { get; set; }
	internal void Retire(bool completed) => _retired.TrySetResult(completed);

	/// <summary>Captures this visit and its current completion round for one admitted tool call.</summary>
	public WorkflowStageCall Capture() => _runner.Capture(this);
	/// <summary>The stage instructions while this execution remains current; <paramref name="continues"/> when its conversation already holds earlier work.</summary>
	public string Compose(bool continues) => _runner.StageBlock(this, continues);
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

/// <summary>What the host needs to keep a stage conversation.</summary>
public sealed record WorkflowStageConversation {
	/// <summary>The record that owns the conversation and the stage that opens it.</summary>
	public required WorkflowConversationRef Key { get; init; }

	/// <summary>The stage that opens the conversation.</summary>
	public required WorkflowStage Opener { get; init; }

	/// <summary>For a fork, the conversation it copies, or <see langword="null"/> for main; <see langword="null"/> for fresh.</summary>
	public required WorkflowConversationRef? Source { get; init; }

	/// <summary>The agent the conversation runs on, or <see langword="null"/> for its source's model or the session's own.</summary>
	public required WorkflowAgent? Agent { get; init; }

	/// <summary>The card's title: the conversation's place in the run, <c>group 2 › task 3</c>.</summary>
	public required string Title { get; init; }
}
