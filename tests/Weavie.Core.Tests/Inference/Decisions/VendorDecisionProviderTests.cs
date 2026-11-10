using System.Net;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

/// <summary>Both decision vendors' wire contracts as verified live, their key, and every failure, over a stubbed handler.</summary>
public sealed class VendorDecisionProviderTests : IDisposable {
	private const string Key = "sk-secret-key";

	private const string OpenAiReply = """
		{"model":"gpt-6-luna","answers":[{"type":"choice","name":"decision","choice":"approve",
		"probabilities":[{"value":"approve","probability":0.85},{"value":"revise","probability":0.15}],"confidence":0.7}],
		"usage":{"input_tokens":233,"input_tokens_details":{"cached_tokens":7,"cache_write_tokens":0},"output_tokens":0}}
		""";

	private const string TypeSafeReply = """
		{"model":"jev-1.14.0","answers":{"decision":{"type":"choice","choice":"approve","confidence":0.86,
		"probabilities":{"approve":0.93,"revise":0.07}}},"usage":{"input_tokens":455,"output_tokens":32}}
		""";

	private readonly TempDirectory _dir = new("weavie-vendor-decision-tests");
	private readonly RecordingHttpHandler _http = new();
	private readonly FileCredentialStore _store;

	public VendorDecisionProviderTests() {
		_store = new FileCredentialStore(_dir.Path);
	}

	public void Dispose() {
		_http.Dispose();
		_dir.Dispose();
	}

	[Theory]
	[InlineData("openai", "https://api.openai.com/v1/decisions",
		"""{"model":"gpt-6-luna","input":[{"role":"user","content":[{"type":"input_json","data":{"review":"clean"}}]}],"questions":[{"type":"choice","name":"decision","instructions":"Approve the change?","choices":[{"value":"approve","description":"The review found nothing to fix."},{"value":"revise"}]}]}""")]
	[InlineData("typesafe", "https://api.typesafe.ai/v1/systemone",
		"""{"model":"jev-latest","state":{"review":"clean"},"questions":{"decision":{"type":"choice","instructions":"Approve the change?","criteria":{"approve":"The review found nothing to fix.","revise":null}}}}""")]
	public async Task PostsTheEvidenceAndOneChoiceQuestion_WithTheSavedKeyAsBearer(string vendor, string url, string body) {
		Connect(vendor);
		_http.Responder = _ => (HttpStatusCode.OK, vendor == DecisionProviderIds.OpenAi ? OpenAiReply : TypeSafeReply);

		await Decide(vendor);

		var request = Assert.Single(_http.Requests);
		Assert.Equal((HttpMethod.Post, url), (request.Method, request.RequestUri!.AbsoluteUri));
		Assert.Equal(("Bearer", Key), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
		Assert.Equal(body, Assert.Single(_http.Bodies));
	}

	[Theory]
	[InlineData("openai", "gpt-6-luna", 0.85, 0.15, 233, 7, 0)]
	[InlineData("typesafe", "jev-1.14.0", 0.93, 0.07, 455, 0, 32)]
	public async Task ReadsTheDistributionModelAndUsage_IgnoringVendorConfidence(
		string vendor, string model, double approve, double revise, long input, long cached, long output) {
		Connect(vendor);
		_http.Responder = _ => (HttpStatusCode.OK, vendor == DecisionProviderIds.OpenAi ? OpenAiReply : TypeSafeReply);

		var distribution = Assert.IsType<DecisionDistribution>(await Decide(vendor));

		Assert.Equal(new Dictionary<string, double> { ["approve"] = approve, ["revise"] = revise }, distribution.Probabilities);
		Assert.Equal(model, distribution.ModelId);
		Assert.Equal(new InferenceUsage { InputTokens = input, CachedInputTokens = cached, OutputTokens = output }, distribution.Usage);
	}

	[Fact]
	public async Task OpenAiRefusal_IsRefused() {
		Connect(DecisionProviderIds.OpenAi);
		_http.Responder = _ => (HttpStatusCode.OK, """{"model":"gpt-6-luna","answers":[{"type":"refusal","name":"decision"}]}""");

		Assert.Equal(InferenceFailureKind.Refused, Assert.IsType<InferenceProviderFailure>(await Decide(DecisionProviderIds.OpenAi)).Kind);
	}

	[Theory]
	[InlineData("openai", """{"model":"gpt-6-luna","answers":[{"type":"choice","name":"other","probabilities":[{"value":"approve","probability":1}]}]}""")]
	[InlineData("openai", """{"model":"gpt-6-luna","answers":[{"type":"choice","name":"decision","probabilities":[{"value":"approve","probability":0.5},{"value":"approve","probability":0.5}]}]}""")]
	[InlineData("openai", "<html>")]
	[InlineData("typesafe", """{"model":"jev-1.14.0","answers":{"other":{"type":"choice","probabilities":{"approve":1}}}}""")]
	[InlineData("typesafe", """{"model":"jev-1.14.0"}""")]
	public async Task MissingOrMalformedAnswer_IsInvalidResponse(string vendor, string reply) {
		Connect(vendor);
		_http.Responder = _ => (HttpStatusCode.OK, reply);

		var failure = Assert.IsType<InferenceProviderFailure>(await Decide(vendor));

		Assert.Equal((InferenceFailureKind.InvalidResponse, $"{Provider(vendor).Label} returned no answer to the decision."), (failure.Kind, failure.Detail));
	}

	[Theory]
	[InlineData(false, "Connect OpenAI to use it (run Connect Service…).")]
	[InlineData(true, "is empty. Connect OpenAI again to use it (run Connect Service…).")]
	public async Task MissingOrUnreadableKey_IsNotConfiguredAndNamesTheConnectCommand(bool unreadable, string detail) {
		if (unreadable) {
			File.WriteAllText(_dir.Combine("openai.key"), " ");
		}

		var failure = Assert.IsType<InferenceProviderFailure>(await Decide(DecisionProviderIds.OpenAi));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Contains(detail, failure.Detail, StringComparison.Ordinal);
		Assert.Empty(_http.Requests);
	}

	[Theory]
	[InlineData("openai", 401, InferenceFailureKind.AuthenticationFailed, "OpenAI rejected its key — run Connect Service… for OpenAI.")]
	[InlineData("typesafe", 429, InferenceFailureKind.RateLimited, "TypeSafe rate-limited the decision.")]
	[InlineData("typesafe", 422, InferenceFailureKind.InputRejected, "TypeSafe rejected the decision request.")]
	[InlineData("openai", 500, InferenceFailureKind.ProviderUnavailable, "OpenAI returned HTTP 500.")]
	public async Task HttpFailure_MapsToItsKindWithoutTheBodyOrKey(string vendor, int status, InferenceFailureKind kind, string detail) {
		Connect(vendor);
		_http.Responder = _ => ((HttpStatusCode)status, $$"""{"detail":[{"msg":"evidence text {{Key}}"}],"raw":"evidence text"}""");

		var failure = Assert.IsType<InferenceProviderFailure>(await Decide(vendor));

		Assert.Equal((kind, detail), (failure.Kind, failure.Detail));
	}

	[Theory]
	[InlineData("openai", 400, """{"error":{"message":"Request too large.","type":"invalid_request_error"}}""", "OpenAI rejected the decision request: Request too large.")]
	[InlineData("typesafe", 400, """{"detail":"Request exceeds 64k tokens…"}""", "TypeSafe rejected the decision request: Request exceeds 64k tokens…")]
	[InlineData("openai", 413, "not json", "OpenAI rejected the decision request.")]
	public async Task OversizedRequest_IsInputRejectedWithOnlyTheVendorMessage(string vendor, int status, string body, string detail) {
		Connect(vendor);
		_http.Responder = _ => ((HttpStatusCode)status, body);

		var failure = Assert.IsType<InferenceProviderFailure>(await Decide(vendor));

		Assert.Equal((InferenceFailureKind.InputRejected, detail), (failure.Kind, failure.Detail));
	}

	[Fact]
	public async Task UnreachableHost_IsProviderUnavailable() {
		Connect(DecisionProviderIds.TypeSafe);
		_http.Responder = _ => throw new HttpRequestException("Name or service not known");

		var failure = Assert.IsType<InferenceProviderFailure>(await Decide(DecisionProviderIds.TypeSafe));

		Assert.Equal((InferenceFailureKind.ProviderUnavailable, "Couldn't reach TypeSafe."), (failure.Kind, failure.Detail));
	}

	[Theory]
	[InlineData("openai", "https://api.openai.com/v1/models", "https://platform.openai.com/api-keys")]
	[InlineData("typesafe", "https://api.typesafe.ai/v1/models", "https://console.typesafe.ai/keys")]
	public async Task Credential_ChecksTheKeyByListingModels(string vendor, string modelsUrl, string keyPage) {
		var credential = Provider(vendor).Credential!;
		_http.Responder = request => request.Headers.Authorization!.Parameter == "good"
			? (HttpStatusCode.OK, """{"models":[]}""")
			: (HttpStatusCode.Unauthorized, "{}");

		Assert.Equal("", await credential.ValidateAsync(new CredentialSecret("good"), CancellationToken.None));
		var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			credential.ValidateAsync(new CredentialSecret("bad"), CancellationToken.None));

		Assert.Contains("rejected", rejected.Message, StringComparison.Ordinal);
		Assert.All(_http.Requests, request => Assert.Equal((HttpMethod.Get, modelsUrl), (request.Method, request.RequestUri!.AbsoluteUri)));
		Assert.Equal((vendor, "API key", keyPage), (credential.Id, credential.KeyNoun, credential.KeyPageUrl));
	}

	private void Connect(string vendor) => _store.Save(vendor, new CredentialSecret(Key));

	private IDecisionProvider Provider(string vendor) => vendor == DecisionProviderIds.OpenAi
		? new OpenAiDecisionProvider(new HttpClient(_http, disposeHandler: false), _store)
		: new TypeSafeDecisionProvider(new HttpClient(_http, disposeHandler: false), _store);

	private Task<InferenceProviderResult> Decide(string vendor) =>
		Provider(vendor).DecideAsync(DecisionServiceTests.Request("/repo"), CancellationToken.None);
}
