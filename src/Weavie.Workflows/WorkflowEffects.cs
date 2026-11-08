namespace Weavie.Workflows;

/// <summary>Something a transition asks the run's owner to do.</summary>
internal abstract record WorkflowEffect {
	/// <summary>Send <paramref name="Turn"/> to the agent as its next turn.</summary>
	public sealed record Kickoff(WorkflowKickoff Turn) : WorkflowEffect;

	/// <summary>Ask the decision model using <paramref name="Plan"/>.</summary>
	public sealed record Decide(WorkflowDecisionPlan Plan) : WorkflowEffect;

	/// <summary>Tell the user <paramref name="Message"/>.</summary>
	public sealed record Notify(string Message) : WorkflowEffect;
}

/// <summary>A run after a transition, and what its owner must do next.</summary>
internal sealed record WorkflowStep(WorkflowRun Run, IReadOnlyList<WorkflowEffect> Effects);

/// <summary>A stage completion's result: the step, and why the outputs were rejected (empty when accepted).</summary>
internal sealed record WorkflowCompletion(WorkflowStep Step, IReadOnlyList<string> Errors);
