using System.Text.Json.Serialization;

namespace Weavie.Workflows;

/// <summary>Where a run is in its current stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowPhase>))]
public enum WorkflowPhase {
	/// <summary>The stage is open; the agent works until it calls completeStage.</summary>
	Open,

	/// <summary>The model is answering a decision (<c>if</c> or <c>then</c>).</summary>
	Deciding,

	/// <summary>A decision waits for the user's answer; see <see cref="WorkflowRun.Asked"/>.</summary>
	Asking,

	/// <summary>The run needs the user before it can continue; see <see cref="WorkflowRun.PausedReason"/>.</summary>
	Paused,

	/// <summary>The run finished.</summary>
	Done,

	/// <summary>The run was cancelled.</summary>
	Cancelled,
}

/// <summary>Which decision a deciding or asking run waits for.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowDecisionKind>))]
public enum WorkflowDecisionKind {
	/// <summary>The stage's <c>if</c>: run it or skip it.</summary>
	If,

	/// <summary>The stage's <c>then</c> decision: where the run goes after it.</summary>
	Then,
}

/// <summary>A decision put to the user: the rendered question, its answers, and why the model didn't answer it.</summary>
public sealed record WorkflowAsked {
	/// <summary>The rendered question.</summary>
	public required string Question { get; init; }

	/// <summary>The answers the user may give.</summary>
	public required IReadOnlyList<string> Choices { get; init; }

	/// <summary>Why the model handed it over (unsure, failed, or its limit reached); empty when the user answers by design.</summary>
	public required string Reason { get; init; }
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

	/// <summary>The decision a deciding or asking run waits for; otherwise <see langword="null"/>.</summary>
	public required WorkflowDecisionKind? Decision { get; init; }

	/// <summary>The question waiting for the user's answer; otherwise <see langword="null"/>.</summary>
	public required WorkflowAsked? Asked { get; init; }

	/// <summary>Rejected completions of the current stage.</summary>
	public required int Attempts { get; init; }

	/// <summary>Each completed stage's reported outputs.</summary>
	public required Dictionary<string, Dictionary<string, WorkflowValue>> Outputs { get; init; }

	/// <summary>The summary the agent gave when it last completed a stage; decisions read it.</summary>
	public required string Summary { get; init; }

	/// <summary>
	/// The model's consecutive answers to a <c>then</c> decision, by stage id; any user answer or another stage's
	/// <c>then</c> decision starts the count over.
	/// </summary>
	public required Dictionary<string, int> Streaks { get; init; }

	/// <summary>Feedback from the user's last answer, owed to the next stage that opens; empty when none.</summary>
	public required string Note { get; init; }

	/// <summary>Why a paused run stopped; otherwise <see langword="null"/>.</summary>
	public required string? PausedReason { get; init; }

	/// <summary>The turn Weavie still owes the agent for the current stage, or <see langword="null"/> once delivered.</summary>
	public required WorkflowKickoff? Kickoff { get; init; }

	/// <summary>Whether the run has ended.</summary>
	[JsonIgnore]
	public bool IsFinished => Phase is WorkflowPhase.Done or WorkflowPhase.Cancelled;
}

/// <summary>A turn Weavie owes the agent: a prompt, or a provider command (a skill) with its arguments.</summary>
public sealed record WorkflowKickoff {
	/// <summary>The turn's full text; a command's starts with <c>/</c> and its name.</summary>
	public required string Text { get; init; }

	/// <summary>The provider command (skill) name, or empty for an ordinary prompt.</summary>
	public required string Command { get; init; }

	/// <summary>An ordinary prompt.</summary>
	public static WorkflowKickoff Prompt(string text) => new() { Text = text, Command = "" };
}
