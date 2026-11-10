using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;

namespace Weavie.Core.Tests.Inference.Decisions;

internal sealed class FakeDecisionProvider(string id, Func<DecisionRequest, CancellationToken, Task<InferenceProviderResult>> decide)
	: IDecisionProvider {
	public FakeDecisionProvider(string id, IReadOnlyDictionary<string, double> probabilities)
		: this(id, (_, _) => Task.FromResult<InferenceProviderResult>(
			new DecisionDistribution { ModelId = "fake-model", RequestId = "req-1", Probabilities = probabilities })) {
	}

	public string Id => id;

	public string Label => id;

	public string Detail => "fake";

	public CredentialDefinition? Credential => null;

	public int Calls { get; private set; }

	public DecisionRequest? LastRequest { get; private set; }

	public Task<InferenceProviderResult> DecideAsync(DecisionRequest request, CancellationToken ct) {
		Calls++;
		LastRequest = request;
		return decide(request, ct);
	}

	public static FakeDecisionProvider Failing(InferenceFailureKind kind) => new(DecisionProviderIds.Agent, (_, _) =>
		Task.FromResult<InferenceProviderResult>(new InferenceProviderFailure { ModelId = "fake-model", Kind = kind, Detail = "safe detail" }));
}
