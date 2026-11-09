using Weavie.Core.Credentials;

namespace Weavie.Core.Inference.Decisions;

/// <summary>Answers a decision with TypeSafe Jev (<c>jev-latest</c>), one attempt over the saved TypeSafe key.</summary>
public sealed class TypeSafeDecisionProvider : IDecisionProvider {
	private const string Model = "jev-latest";
	private const string Question = "decision";

	private readonly HttpClient _http;
	private readonly ICredentialStore _store;

	/// <summary>Creates the provider over <paramref name="http"/> and the key saved in <paramref name="store"/>.</summary>
	public TypeSafeDecisionProvider(HttpClient http, ICredentialStore store) {
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(store);
		_http = http;
		_store = store;
		Credential = VendorHttp.Credential(
			http, DecisionProviderIds.TypeSafe, "TypeSafe", "https://console.typesafe.ai/keys", "https://api.typesafe.ai/v1/models");
	}

	/// <inheritdoc/>
	public string Id => DecisionProviderIds.TypeSafe;

	/// <inheritdoc/>
	public string Label => Credential.DisplayName;

	/// <inheritdoc/>
	public string Detail => "TypeSafe Jev, calibrated probabilities in ~0.1s";

	/// <inheritdoc/>
	public CredentialDefinition Credential { get; }

	/// <inheritdoc/>
	public Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var body = new TypeSafeDecisionRequest(
			Model,
			request.Evidence,
			new Dictionary<string, TypeSafeQuestion> {
				[Question] = new("choice", request.Question.Instructions,
					request.Question.Choices.ToDictionary(choice => choice.Label, choice => choice.Description)),
			});
		return VendorHttp.DecideAsync(
			_http, _store, Credential, "https://api.typesafe.ai/v1/systemone", body,
			DecisionJson.Default.TypeSafeDecisionRequest, DecisionJson.Default.TypeSafeDecisionResponse, Read, ct);
	}

	private DecisionProviderResult Read(TypeSafeDecisionResponse response, string? requestId) {
		string model = response.Model ?? Model;
		var usage = response.Usage is { } reported
			? new InferenceUsage { InputTokens = reported.InputTokens, CachedInputTokens = 0, OutputTokens = reported.OutputTokens }
			: null;
		if (response.Answers?.GetValueOrDefault(Question)?.Probabilities is not { } probabilities) {
			return VendorHttp.Missing(Label, requestId) with { ModelId = model, Usage = usage };
		}
		return new DecisionDistribution { ModelId = model, RequestId = requestId, Usage = usage, Probabilities = probabilities };
	}
}
