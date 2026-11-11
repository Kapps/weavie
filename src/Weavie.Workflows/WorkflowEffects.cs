namespace Weavie.Workflows;

/// <summary>Something a transition asks the run's owner to do.</summary>
internal abstract record WorkflowEffect {
	/// <summary>Enter a distinct stage visit, even when the stage name repeats.</summary>
	public sealed record EnterStage : WorkflowEffect;

	/// <summary>Keep the stage visit and admit a new completion round.</summary>
	public sealed record ReviseStage : WorkflowEffect;

	/// <summary>Send <paramref name="Turn"/> to the agent as its next turn.</summary>
	public sealed record Kickoff(WorkflowKickoff Turn) : WorkflowEffect;

	/// <summary>Ask the decision model using <paramref name="Plan"/>.</summary>
	public sealed record Decide(WorkflowDecisionPlan Plan) : WorkflowEffect;

	/// <summary>Tell the user <paramref name="Message"/>.</summary>
	public sealed record Notify(string Message) : WorkflowEffect;
}

/// <summary>What a new run starts with.</summary>
internal sealed record WorkflowRunStart {
	/// <summary>The new run's id.</summary>
	public required string RunId { get; init; }

	/// <summary>Where the workflow's file lives.</summary>
	public required string SourcePath { get; init; }

	/// <summary>The run's inputs by name.</summary>
	public required Dictionary<string, WorkflowValue> Inputs { get; init; }

	/// <summary>The run's shared documents directory.</summary>
	public required string DocumentsDirectory { get; init; }

	/// <summary>The predecessor's hold, whose own values are still owed back to the session.</summary>
	public required WorkflowHold? Hold { get; init; }

	/// <summary>What the run was started with.</summary>
	public required WorkflowPrompt Prompt { get; init; }

	/// <summary>The agent filling each of the definition's roles, named for it.</summary>
	public required IReadOnlyList<WorkflowAgent> Roles { get; init; }
}

/// <summary>A run after a transition, and what its owner must do next.</summary>
internal sealed record WorkflowStep(WorkflowRun Run, IReadOnlyList<WorkflowEffect> Effects);

/// <summary>A stage completion's result: the step, and why the outputs were rejected (empty when accepted).</summary>
internal sealed record WorkflowCompletion(WorkflowStep Step, IReadOnlyList<string> Errors);
