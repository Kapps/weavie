using System.Text.Json.Serialization;

namespace Weavie.Workflows;

/// <summary>Where a run is in its current stage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowPhase>))]
public enum WorkflowPhase {
	/// <summary>The stage is open; the agent works until it completes the stage.</summary>
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

	/// <summary>Why the model handed it over (low confidence, refusal, failure, or its limit reached); empty when the user answers by design.</summary>
	public required string Reason { get; init; }
}

/// <summary>One execution of a workflow, owned by a session and persisted with it.</summary>
public sealed record WorkflowRun {
	/// <summary>The run's id (<c>r-</c> plus 12 hex digits).</summary>
	public required string Id { get; init; }

	/// <summary>The shared documents directory outside every session worktree.</summary>
	public required string DocumentsDirectory { get; init; }

	/// <summary>The workflow id.</summary>
	public required string WorkflowId { get; init; }

	/// <summary>Where the workflow's file lived when the run started.</summary>
	public required string SourcePath { get; init; }

	/// <summary>The definition text the run executes; editing the file never changes a run in flight.</summary>
	public required string SourceText { get; init; }

	/// <summary>The run's inputs by name.</summary>
	public required Dictionary<string, WorkflowValue> Inputs { get; init; }

	/// <summary>What the run was started with.</summary>
	public WorkflowPrompt Prompt { get; set; } = WorkflowPrompt.None;

	/// <summary>Whether the main conversation has been sent the prompt's images.</summary>
	public bool PromptImagesSent { get; set; }

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

	/// <summary>Completed independent stages' reports owed to the main conversation until its next turn is claimed.</summary>
	public required string Unreported { get; init; }

	/// <summary>
	/// The model's consecutive answers to a <c>then</c> decision, by stage id; any user answer or another stage's
	/// <c>then</c> decision starts the count over.
	/// </summary>
	public required Dictionary<string, int> Streaks { get; init; }

	/// <summary>Feedback from the user's last answer, owed to the next stage that opens; empty when none.</summary>
	public required string Note { get; init; }

	/// <summary>Why a paused run stopped; otherwise <see langword="null"/>.</summary>
	public required string? PausedReason { get; init; }

	/// <summary>The turn the host still owes the agent for the current stage, or <see langword="null"/> once delivered.</summary>
	public required WorkflowKickoff? Kickoff { get; init; }

	/// <summary>The session's control values the run holds, or <see langword="null"/> while it holds none.</summary>
	public required WorkflowHold? Hold { get; init; }

	/// <summary>Whether the run has ended.</summary>
	[JsonIgnore]
	public bool IsFinished => Phase is WorkflowPhase.Done or WorkflowPhase.Cancelled;
}

/// <summary>A turn the host owes the agent: a prompt, or a provider command (a skill) with its arguments.</summary>
public sealed record WorkflowKickoff {
	/// <summary>The conversation receiving this turn; questions and completion reports always use main.</summary>
	public required WorkflowContext Context { get; init; }

	/// <summary>The turn's full text; a command's starts with <c>/</c> and its name.</summary>
	public required string Text { get; init; }

	/// <summary>The provider command (skill) name, or empty for an ordinary prompt.</summary>
	public required string Command { get; init; }

	/// <summary>The stage this turn opens, or empty for a question or a turn within the open stage.</summary>
	public required string Opens { get; init; }

	/// <summary>Whether the turn carries the images of the run's prompt.</summary>
	public bool CarriesPromptImages { get; init; }

	/// <summary>An ordinary prompt within the run's current stage.</summary>
	public static WorkflowKickoff Prompt(string text) => new() { Context = WorkflowContext.Continue, Text = text, Command = "", Opens = "" };
}

/// <summary>
/// Control values a run keeps in effect on the session's conversation: the session's own values, to restore when the
/// run no longer holds any, and the values the current stage holds over them. Keys are the provider's axis ids.
/// </summary>
public sealed record WorkflowHold {
	/// <summary>The name of the agent the values were held for.</summary>
	public required string Agent { get; init; }

	/// <summary>The stage the values were held for; once the run moves on, they are that stage's until the next opens.</summary>
	public required string Stage { get; init; }

	/// <summary>The session's own value of every axis the run may change.</summary>
	public required Dictionary<string, string> Own { get; init; }

	/// <summary>The values the current stage holds: its agent's model and options, then any stage-only picks.</summary>
	public required Dictionary<string, string> Held { get; init; }
}
