using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Sessions;

namespace Weavie.Core.Workflows;

/// <summary>What a run is doing, as the session list shows it.</summary>
[JsonConverter(typeof(WorkflowRunStateConverter))]
public enum WorkflowRunState {
	/// <summary>The agent is working on the stage.</summary>
	Working,

	/// <summary>The stage is open and the agent is idle: it's the user's turn.</summary>
	WaitingOnYou,

	/// <summary>A decision waits for the user's answer.</summary>
	NeedsAnswer,

	/// <summary>The stage is open and the agent has background work pending.</summary>
	Watching,

	/// <summary>A decision is being made.</summary>
	Deciding,

	/// <summary>The run needs the user before it continues.</summary>
	Paused,

	/// <summary>The agent stopped with the stage open.</summary>
	Stalled,
}

/// <summary>A live run's identity, stage, and state; finished runs have none.</summary>
public sealed record WorkflowRunSummary {
	/// <summary>The run id.</summary>
	public required string RunId { get; init; }

	/// <summary>The workflow id.</summary>
	public required string Workflow { get; init; }

	/// <summary>The current stage id.</summary>
	public required string Stage { get; init; }

	/// <summary>What the run is doing.</summary>
	public required WorkflowRunState State { get; init; }

	/// <summary>Why the run paused, or the question awaiting the user (with why the model didn't answer it); otherwise empty.</summary>
	public required string Reason { get; init; }

	/// <summary>The summary of <paramref name="run"/> in a session whose agent is <paramref name="status"/>.</summary>
	public static WorkflowRunSummary? From(WorkflowRun? run, SessionStatus status) {
		if (run is null || run.IsFinished) {
			return null;
		}

		var state = run.Phase switch {
			WorkflowPhase.Asking => WorkflowRunState.NeedsAnswer,
			WorkflowPhase.Deciding => WorkflowRunState.Deciding,
			WorkflowPhase.Paused => WorkflowRunState.Paused,
			_ => status switch {
				SessionStatus.Idle or SessionStatus.NeedsInput => WorkflowRunState.WaitingOnYou,
				SessionStatus.Waiting => WorkflowRunState.Watching,
				SessionStatus.Error => WorkflowRunState.Stalled,
				_ => WorkflowRunState.Working,
			},
		};
		return new WorkflowRunSummary {
			RunId = run.Id,
			Workflow = run.WorkflowId,
			Stage = run.Stage,
			State = state,
			Reason = state switch {
				WorkflowRunState.Paused => run.PausedReason ?? "",
				WorkflowRunState.NeedsAnswer => run.Asked!.Reason.Length == 0 ? run.Asked.Question : $"{run.Asked.Question} ({run.Asked.Reason})",
				_ => "",
			},
		};
	}
}

/// <summary>Writes <see cref="WorkflowRunState"/> in camelCase, as the web reads it.</summary>
public sealed class WorkflowRunStateConverter() : JsonStringEnumConverter<WorkflowRunState>(JsonNamingPolicy.CamelCase);
