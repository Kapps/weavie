using Weavie.Core.Credentials;

namespace Weavie.Core.Inference.Decisions;

/// <summary>Makes one attempt at one decision question, returning a probability for every choice.</summary>
public interface IDecisionProvider {
	/// <summary>The stable id named by <c>inference.decisionProvider</c>.</summary>
	string Id { get; }

	/// <summary>The display name.</summary>
	string Label { get; }

	/// <summary>A one-line description for pickers.</summary>
	string Detail { get; }

	/// <summary>The key the provider calls with, or null when it needs none.</summary>
	CredentialDefinition? Credential { get; }

	/// <summary>Runs one attempt: a <see cref="DecisionDistribution"/>, or an <see cref="InferenceProviderFailure"/> for a
	/// refusal or any other non-cancellation failure.</summary>
	Task<InferenceProviderResult> DecideAsync(DecisionRequest request, CancellationToken ct);
}
