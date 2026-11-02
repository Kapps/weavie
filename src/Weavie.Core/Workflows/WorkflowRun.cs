using System.Text.Json.Serialization;

namespace Weavie.Core.Workflows;

/// <summary>Where a run is in its current stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowPhase>))]
public enum WorkflowPhase {
	/// <summary>The stage is open; the agent works until it calls completeStage.</summary>
	Open,

	/// <summary>The stage completed and waits for the user's approval.</summary>
	AwaitingApproval,

	/// <summary>A decision (<c>when</c> or <c>next</c>) is being made.</summary>
	Deciding,

	/// <summary>The run needs the user before it can continue; see <see cref="WorkflowRun.PausedReason"/>.</summary>
	Paused,

	/// <summary>The run finished.</summary>
	Done,

	/// <summary>The run was cancelled.</summary>
	Cancelled,
}

/// <summary>Which decision a <see cref="WorkflowPhase.Deciding"/> run waits for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowDecisionKind>))]
public enum WorkflowDecisionKind {
	/// <summary>The stage's <c>when</c>: run it or skip it.</summary>
	When,

	/// <summary>The stage's <c>next</c> choice: where the run goes after it.</summary>
	Next,
}

/// <summary>One execution of a workflow, owned by a session and persisted with it.</summary>
public sealed record WorkflowRun {
	/// <summary>The run's id (<c>r-</c> plus 12 hex digits).</summary>
	public required string Id { get; init; }

	/// <summary>The workflow id.</summary>
	public required string WorkflowId { get; init; }

	/// <summary>Where the workflow's file lived when the run started.</summary>
	public required string SourcePath { get; init; }

	/// <summary>The definition text the run executes; editing the file never changes a run in flight.</summary>
	public required string SourceText { get; init; }

	/// <summary>The run's inputs by name.</summary>
	public required Dictionary<string, WorkflowValue> Inputs { get; init; }

	/// <summary>The current stage id.</summary>
	public required string Stage { get; init; }

	/// <summary>Where the run is in that stage.</summary>
	public required WorkflowPhase Phase { get; init; }

	/// <summary>The decision a deciding run waits for; otherwise <see langword="null"/>.</summary>
	public required WorkflowDecisionKind? Deciding { get; init; }

	/// <summary>Rejected completions of the current stage.</summary>
	public required int Attempts { get; init; }

	/// <summary>Each completed stage's reported outputs.</summary>
	public required Dictionary<string, Dictionary<string, WorkflowValue>> Outputs { get; init; }

	/// <summary>How often each <c>from→to</c> edge of a choice <c>next</c> has been taken.</summary>
	public required Dictionary<string, int> EdgeCounts { get; init; }

	/// <summary>Why a paused run stopped; otherwise <see langword="null"/>.</summary>
	public required string? PausedReason { get; init; }

	/// <summary>The turn Weavie still owes the agent for the current stage, or <see langword="null"/> once delivered.</summary>
	public required string? Kickoff { get; init; }

	/// <summary>Whether the run has ended.</summary>
	[JsonIgnore]
	public bool IsFinished => Phase is WorkflowPhase.Done or WorkflowPhase.Cancelled;
}
