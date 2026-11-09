using System.Net;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

/// <summary>TypeSafe System One's wire contract as verified live, over a stubbed handler.</summary>
public sealed class TypeSafeDecisionProviderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-typesafe-decision-tests");
	private readonly RecordingHttpHandler _http = new();
	private readonly TypeSafeDecisionProvider _provider;

	public TypeSafeDecisionProviderTests() {
		var store = new FileCredentialStore(_dir.Path);
		store.Save(DecisionProviderIds.TypeSafe, new CredentialSecret("ts-x"));
		_provider = new TypeSafeDecisionProvider(new HttpClient(_http), store);
	}

	public void Dispose() {
		_http.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task PostsTheEvidenceAsStateAndOneChoiceQuestion_SendingNullForAnAbsentDescription() {
		_http.Responder = _ => (HttpStatusCode.OK, Reply);

		await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None);

		Assert.Equal("https://api.typesafe.ai/v1/systemone", Assert.Single(_http.Requests).RequestUri!.AbsoluteUri);
		Assert.Equal(
			"""{"model":"jev-latest","state":{"review":"clean"},"questions":{"decision":{"type":"choice","instructions":"Approve the change?","criteria":{"approve":"The review found nothing to fix.","revise":null}}}}""",
			Assert.Single(_http.Bodies));
	}

	[Fact]
	public async Task ReadsTheDistributionModelAndUsage_IgnoringVendorConfidence() {
		_http.Responder = _ => (HttpStatusCode.OK, Reply);

		var distribution = Assert.IsType<DecisionDistribution>(
			await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None));

		Assert.Equal(new Dictionary<string, double> { ["approve"] = 0.93, ["revise"] = 0.07 }, distribution.Probabilities);
		Assert.Equal("jev-1.14.0", distribution.ModelId);
		Assert.Equal(new InferenceUsage { InputTokens = 455, CachedInputTokens = 0, OutputTokens = 32 }, distribution.Usage);
	}

	[Theory]
	[InlineData("""{"model":"jev-1.14.0","answers":{"other":{"type":"choice","probabilities":{"approve":1}}}}""")]
	[InlineData("""{"model":"jev-1.14.0"}""")]
	public async Task MissingAnswer_IsInvalidResponse(string reply) {
		_http.Responder = _ => (HttpStatusCode.OK, reply);

		var failure = Assert.IsType<DecisionProviderFailure>(
			await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None));

		Assert.Equal((InferenceFailureKind.InvalidResponse, "TypeSafe returned no answer to the decision."), (failure.Kind, failure.Detail));
	}

	private const string Reply = """
		{"model":"jev-1.14.0","answers":{"decision":{"type":"choice","choice":"approve","confidence":0.86,
		"probabilities":{"approve":0.93,"revise":0.07}}},"usage":{"input_tokens":455,"output_tokens":32}}
		""";
}
