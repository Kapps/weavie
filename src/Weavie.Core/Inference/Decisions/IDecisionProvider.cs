using System.Text.Json;

namespace Weavie.Core.Inference.Decisions;

/// <summary>Makes one attempt at one decision question, returning a probability for every choice.</summary>
public interface IDecisionProvider {
	/// <summary>The stable id named by <c>inference.decisionProvider</c>.</summary>
	string Id { get; }

	/// <summary>The display name.</summary>
	string Label { get; }

	/// <summary>A one-line description for pickers.</summary>
	string Detail { get; }

	/// <summary>Whether the provider needs a connected credential.</summary>
	bool NeedsCredential { get; }

	/// <summary>Runs one attempt; non-cancellation failures are returned.</summary>
	Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct);
}

/// <summary>One validated decision handed to a provider.</summary>
public sealed record DecisionProviderRequest {
	/// <summary>The session worktree that owns the decision.</summary>
	public required InferenceOwner Owner { get; init; }

	/// <summary>The serialized evidence.</summary>
	public required JsonElement Evidence { get; init; }

	/// <summary>The question to answer.</summary>
	public required DecisionQuestion Question { get; init; }

	/// <summary>Whether a person directly initiated the decision.</summary>
	public required InferenceInvocationOrigin Origin { get; init; }

	/// <summary>The attempt's time budget.</summary>
	public required TimeSpan TimeBudget { get; init; }
}

/// <summary>A provider's raw, unvalidated outcome.</summary>
public abstract record DecisionProviderResult {
	/// <summary>The concrete model that answered, or empty when none was reached.</summary>
	public required string ModelId { get; init; }

	/// <summary>The provider request id, when one was returned.</summary>
	public string? RequestId { get; init; }

	/// <summary>Provider-reported token usage, when one was returned.</summary>
	public InferenceUsage? Usage { get; init; }
}

/// <summary>A probability per label, validated by <see cref="DecisionService"/>.</summary>
public sealed record DecisionDistribution : DecisionProviderResult {
	/// <summary>The provider's probability for each label it returned.</summary>
	public required IReadOnlyDictionary<string, double> Probabilities { get; init; }
}

/// <summary>A refusal or failure reported by the provider.</summary>
public sealed record DecisionProviderFailure : DecisionProviderResult {
	/// <summary>The stable failure category.</summary>
	public required InferenceFailureKind Kind { get; init; }

	/// <summary>A sanitized diagnostic.</summary>
	public required string Detail { get; init; }
}
