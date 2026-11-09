using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;

namespace Weavie.Core.Tests.Inference.Decisions;

internal sealed class FakeDecisionProvider(
	string id,
	Func<DecisionProviderRequest, CancellationToken, Task<DecisionProviderResult>> decide) : IDecisionProvider {
	public FakeDecisionProvider(string id, IReadOnlyDictionary<string, double> probabilities)
		: this(id, (_, _) => Task.FromResult<DecisionProviderResult>(Distribution(probabilities))) {
	}

	public string Id => id;

	public string Label => id;

	public string Detail => "fake";

	public bool NeedsCredential => false;

	public int Calls { get; private set; }

	public DecisionProviderRequest? LastRequest { get; private set; }

	public Task<DecisionProviderResult> DecideAsync(DecisionProviderRequest request, CancellationToken ct) {
		Calls++;
		LastRequest = request;
		return decide(request, ct);
	}

	public static DecisionDistribution Distribution(IReadOnlyDictionary<string, double> probabilities) => new() {
		ModelId = "fake-model",
		RequestId = "req-1",
		Probabilities = probabilities,
	};

	public static DecisionProviderFailure Failure(InferenceFailureKind kind) => new() {
		ModelId = "fake-model",
		Kind = kind,
		Detail = "safe detail",
	};
}
