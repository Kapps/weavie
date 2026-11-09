namespace Weavie.Core.Inference.Decisions;

/// <summary>One answer a decision may choose, with optional criteria distinguishing it.</summary>
/// <param name="Label">The label the decision returns.</param>
/// <param name="Description">What makes this the right answer, or null.</param>
public sealed record DecisionChoice(string Label, string? Description);

/// <summary>One choice question asked over the decision's evidence.</summary>
/// <param name="Instructions">What to decide.</param>
/// <param name="Choices">At least two choices with unique labels.</param>
public sealed record DecisionQuestion(string Instructions, IReadOnlyList<DecisionChoice> Choices);

/// <summary>Execution policy for one decision.</summary>
public sealed record DecisionQueryOptions {
	/// <summary>Whether a person directly initiated the decision.</summary>
	public required InferenceInvocationOrigin Origin { get; init; }

	/// <summary>The single attempt's time budget.</summary>
	public required TimeSpan TimeBudget { get; init; }
}

/// <summary>The exhaustive result of one decision.</summary>
public abstract record DecisionResult;

/// <summary>A validated distribution and the label it chose.</summary>
public sealed record DecisionAnswer : DecisionResult {
	/// <summary>The label with the highest probability.</summary>
	public required string Choice { get; init; }

	/// <summary>The probability of <see cref="Choice"/>.</summary>
	public required double Probability { get; init; }

	/// <summary>The provider's probability for every label.</summary>
	public required IReadOnlyDictionary<string, double> Probabilities { get; init; }

	/// <summary>Non-content observability for the attempt.</summary>
	public required InferenceReceipt Receipt { get; init; }
}

/// <summary>A non-cancellation failure, including a refusal (<see cref="InferenceFailureKind.Refused"/>).</summary>
public sealed record DecisionFailure : DecisionResult {
	/// <summary>The stable failure category.</summary>
	public required InferenceFailureKind Kind { get; init; }

	/// <summary>A sanitized diagnostic containing no evidence, credential, or raw provider body.</summary>
	public required string Detail { get; init; }

	/// <summary>Attempt metadata when a provider call began.</summary>
	public InferenceReceipt? Receipt { get; init; }
}
