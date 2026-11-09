using System.Net;
using System.Text.Json;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

/// <summary>OpenAI Decisions' wire contract as verified live, over a stubbed handler.</summary>
public sealed class OpenAiDecisionProviderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-openai-decision-tests");
	private readonly RecordingHttpHandler _http = new();
	private readonly OpenAiDecisionProvider _provider;

	public OpenAiDecisionProviderTests() {
		var store = new FileCredentialStore(_dir.Path);
		store.Save(DecisionProviderIds.OpenAi, new CredentialSecret("sk-x"));
		_provider = new OpenAiDecisionProvider(new HttpClient(_http), store);
	}

	public void Dispose() {
		_http.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task PostsTheEvidenceAsInputJsonAndOneChoiceQuestion_OmittingAnAbsentDescription() {
		_http.Responder = _ => (HttpStatusCode.OK, Reply);

		await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None);

		Assert.Equal("https://api.openai.com/v1/decisions", Assert.Single(_http.Requests).RequestUri!.AbsoluteUri);
		Assert.Equal(
			"""{"model":"gpt-6-luna","input":[{"role":"user","content":[{"type":"input_json","data":{"review":"clean"}}]}],"questions":[{"type":"choice","name":"decision","instructions":"Approve the change?","choices":[{"value":"approve","description":"The review found nothing to fix."},{"value":"revise"}]}]}""",
			Assert.Single(_http.Bodies));
	}

	[Fact]
	public async Task ReadsTheDistributionModelAndUsage_IgnoringVendorConfidence() {
		_http.Responder = _ => (HttpStatusCode.OK, Reply);

		var distribution = Assert.IsType<DecisionDistribution>(
			await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None));

		Assert.Equal(new Dictionary<string, double> { ["approve"] = 0.85, ["revise"] = 0.15 }, distribution.Probabilities);
		Assert.Equal("gpt-6-luna", distribution.ModelId);
		Assert.Equal(new InferenceUsage { InputTokens = 233, CachedInputTokens = 7, OutputTokens = 0 }, distribution.Usage);
	}

	[Fact]
	public async Task RefusalAnswer_IsRefused() {
		_http.Responder = _ => (HttpStatusCode.OK, """{"model":"gpt-6-luna","answers":[{"type":"refusal","name":"decision"}]}""");

		var failure = Assert.IsType<DecisionProviderFailure>(
			await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.Refused, failure.Kind);
	}

	[Theory]
	[InlineData("""{"model":"gpt-6-luna","answers":[{"type":"choice","name":"other","choice":"approve","probabilities":[{"value":"approve","probability":1}]}]}""")]
	[InlineData("""{"model":"gpt-6-luna","answers":[]}""")]
	[InlineData("""{"model":"gpt-6-luna","answers":[{"type":"choice","name":"decision","probabilities":[{"value":"approve","probability":0.5},{"value":"approve","probability":0.5}]}]}""")]
	public async Task MissingAnswer_IsInvalidResponse(string reply) {
		_http.Responder = _ => (HttpStatusCode.OK, reply);

		var failure = Assert.IsType<DecisionProviderFailure>(
			await _provider.DecideAsync(VendorDecisionProviderTests.Request(), CancellationToken.None));

		Assert.Equal((InferenceFailureKind.InvalidResponse, "OpenAI returned no answer to the decision."), (failure.Kind, failure.Detail));
	}

	private const string Reply = """
		{"model":"gpt-6-luna","answers":[{"type":"choice","name":"decision","choice":"approve",
		"probabilities":[{"value":"approve","probability":0.85},{"value":"revise","probability":0.15}],"confidence":0.7}],
		"usage":{"input_tokens":233,"input_tokens_details":{"cached_tokens":7,"cache_write_tokens":0},"output_tokens":0}}
		""";
}
