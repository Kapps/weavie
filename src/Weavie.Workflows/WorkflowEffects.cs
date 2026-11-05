namespace Weavie.Workflows;

/// <summary>Something a transition asks the run's owner to do.</summary>
internal abstract record WorkflowEffect {
	/// <summary>Send <paramref name="Turn"/> to the agent as its next turn.</summary>
	public sealed record Kickoff(WorkflowKickoff Turn) : WorkflowEffect;

	/// <summary>Ask the decision model <paramref name="Request"/>.</summary>
	public sealed record Decide(WorkflowDecisionRequest Request) : WorkflowEffect;

	/// <summary>Tell the user <paramref name="Message"/>.</summary>
	public sealed record Notify(string Message) : WorkflowEffect;
}

/// <summary>A run after a transition, and what its owner must do next.</summary>
internal sealed record WorkflowStep(WorkflowRun Run, IReadOnlyList<WorkflowEffect> Effects);

/// <summary>A stage completion's result: the step, and why the outputs were rejected (empty when accepted).</summary>
internal sealed record WorkflowCompletion(WorkflowStep Step, IReadOnlyList<string> Errors);

/// <summary>One typed decision: a question and the answers it may take.</summary>
public sealed record WorkflowDecisionRequest {
	/// <summary>The rendered question.</summary>
	public required string Question { get; init; }

	/// <summary>The allowed answers, without the implicit <see cref="WorkflowDecision.Unsure"/>.</summary>
	public required IReadOnlyList<string> Choices { get; init; }

	/// <summary>The outputs of the stage the decision follows, rendered; empty for an <c>if</c>.</summary>
	public required IReadOnlyDictionary<string, string> Outputs { get; init; }

	/// <summary>The summary the agent gave when it last completed a stage, or empty.</summary>
	public required string Summary { get; init; }
}

/// <summary>A decision's answer, or why there is none.</summary>
public sealed record WorkflowDecisionResult {
	/// <summary>The chosen answer, or <see langword="null"/> when the decision failed.</summary>
	public required string? Choice { get; init; }

	/// <summary>Why the decision failed; empty when <see cref="Choice"/> is set.</summary>
	public required string Failure { get; init; }

	/// <summary>A decided answer.</summary>
	public static WorkflowDecisionResult Chose(string choice) => new() { Choice = choice, Failure = "" };

	/// <summary>A failed decision.</summary>
	public static WorkflowDecisionResult Failed(string failure) => new() { Choice = null, Failure = failure };
}
