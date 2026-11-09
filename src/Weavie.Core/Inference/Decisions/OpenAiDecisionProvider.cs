using Weavie.Core.Credentials;

namespace Weavie.Core.Inference.Decisions;

/// <summary>Answers a decision with OpenAI Decisions (<c>gpt-6-luna</c>), one attempt over the saved OpenAI key.</summary>
public sealed class OpenAiDecisionProvider : VendorDecisionProvider {
	private const string Model = "gpt-6-luna";

	/// <summary>Creates the provider over <paramref name="http"/> and the key saved in <paramref name="store"/>.</summary>
	public OpenAiDecisionProvider(HttpClient http, ICredentialStore store)
		: base(http, store, DecisionProviderIds.OpenAi, "OpenAI", "https://platform.openai.com/api-keys", "https://api.openai.com/v1/models") {
	}

	/// <inheritdoc/>
	public override string Detail => "OpenAI Decisions (gpt-6-luna), calibrated probabilities in ~0.2s";

	/// <inheritdoc/>
	public override Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var body = new OpenAiDecisionRequest(
			Model,
			[new OpenAiInput("user", [new OpenAiInputJson("input_json", request.Evidence)])],
			[new OpenAiQuestion("choice", Question, request.Question.Instructions,
				[.. request.Question.Choices.Select(choice => new OpenAiChoice(choice.Label, choice.Description))])]);
		return PostAsync(
			"https://api.openai.com/v1/decisions", body,
			DecisionJson.Default.OpenAiDecisionRequest, DecisionJson.Default.OpenAiDecisionResponse, Read, ct);
	}

	private DecisionProviderResult Read(OpenAiDecisionResponse response, string? requestId) {
		string model = response.Model ?? Model;
		var usage = response.Usage is { } reported
			? new InferenceUsage {
				InputTokens = reported.InputTokens,
				CachedInputTokens = reported.InputTokensDetails?.CachedTokens ?? 0,
				OutputTokens = reported.OutputTokens,
			}
			: null;
		var answer = response.Answers?.FirstOrDefault(candidate => candidate.Name == Question);
		if (answer?.Type == "refusal") {
			return new DecisionProviderFailure {
				ModelId = model,
				RequestId = requestId,
				Usage = usage,
				Kind = InferenceFailureKind.Refused,
				Detail = "OpenAI refused the decision.",
			};
		}
		if (answer?.Probabilities is not { } probabilities
			|| probabilities.DistinctBy(entry => entry.Value).Count() != probabilities.Count) {
			return Missing(Label, requestId) with { ModelId = model, Usage = usage };
		}
		return new DecisionDistribution {
			ModelId = model,
			RequestId = requestId,
			Usage = usage,
			Probabilities = probabilities.ToDictionary(entry => entry.Value, entry => entry.Probability),
		};
	}
}
