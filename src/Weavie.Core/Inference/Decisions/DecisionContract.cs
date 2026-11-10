using System.Text.Json;

namespace Weavie.Core.Inference.Decisions;

/// <summary>One answer a decision may choose, with optional criteria distinguishing it.</summary>
/// <param name="Label">The label the decision returns.</param>
/// <param name="Description">What makes this the right answer, or null.</param>
public sealed record DecisionChoice(string Label, string? Description);

/// <summary>One choice question asked over the decision's evidence.</summary>
/// <param name="Instructions">What to decide.</param>
/// <param name="Choices">At least two choices with unique labels.</param>
public sealed record DecisionQuestion(string Instructions, IReadOnlyList<DecisionChoice> Choices);

/// <summary>One decision: a question over serialized evidence, owned by a session worktree.</summary>
public sealed record DecisionRequest {
	/// <summary>The session worktree that owns the decision.</summary>
	public required InferenceOwner Owner { get; init; }

	/// <summary>The serialized evidence.</summary>
	public required JsonElement Evidence { get; init; }

	/// <summary>The question to answer.</summary>
	public required DecisionQuestion Question { get; init; }

	/// <summary>Whether a person directly initiated the decision.</summary>
	public required InferenceInvocationOrigin Origin { get; init; }

	/// <summary>The single attempt's time budget.</summary>
	public required TimeSpan TimeBudget { get; init; }
}

/// <summary>A provider's probability for each label it returned, validated by <see cref="DecisionService"/>.</summary>
public sealed record DecisionDistribution : InferenceProviderResult {
	/// <summary>The probability per returned label.</summary>
	public required IReadOnlyDictionary<string, double> Probabilities { get; init; }
}

/// <summary>A validated distribution and the label it chose.</summary>
/// <param name="Choice">The label with the highest probability.</param>
/// <param name="Probability">The probability of <paramref name="Choice"/>.</param>
/// <param name="Probabilities">The provider's probability for every label.</param>
public sealed record DecisionAnswer(string Choice, double Probability, IReadOnlyDictionary<string, double> Probabilities);
