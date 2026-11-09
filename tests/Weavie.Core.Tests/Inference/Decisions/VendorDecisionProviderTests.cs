using System.Net;
using System.Text.Json;
using Weavie.Core.Credentials;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

/// <summary>What both decision vendors share over a stubbed handler: the key, its check, and every HTTP failure.</summary>
public sealed class VendorDecisionProviderTests : IDisposable {
	private const string Key = "sk-secret-key";

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

	public static TheoryData<string> Vendors => [DecisionProviderIds.OpenAi, DecisionProviderIds.TypeSafe];

	[Theory]
	[MemberData(nameof(Vendors))]
	public async Task MissingKey_IsNotConfiguredAndNamesTheConnectCommand(string vendor) {
		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Equal($"Connect {Provider(vendor).Label} to use it (run Connect Service…).", failure.Detail);
		Assert.Empty(_http.Requests);
	}

	[Theory]
	[MemberData(nameof(Vendors))]
	public async Task UnreadableKey_IsNotConfigured(string vendor) {
		File.WriteAllText(_dir.Combine($"{vendor}.key"), " ");

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Contains("is empty", failure.Detail, StringComparison.Ordinal);
		Assert.Empty(_http.Requests);
	}

	[Theory]
	[InlineData("openai", 401, InferenceFailureKind.AuthenticationFailed, "OpenAI rejected its key — run Connect Service… for OpenAI.")]
	[InlineData("typesafe", 403, InferenceFailureKind.AuthenticationFailed, "TypeSafe rejected its key — run Connect Service… for TypeSafe.")]
	[InlineData("openai", 429, InferenceFailureKind.RateLimited, "OpenAI rate-limited the decision.")]
	[InlineData("typesafe", 429, InferenceFailureKind.RateLimited, "TypeSafe rate-limited the decision.")]
	[InlineData("typesafe", 422, InferenceFailureKind.InputRejected, "TypeSafe rejected the decision request.")]
	[InlineData("openai", 500, InferenceFailureKind.ProviderUnavailable, "OpenAI returned HTTP 500.")]
	[InlineData("typesafe", 529, InferenceFailureKind.ProviderUnavailable, "TypeSafe returned HTTP 529.")]
	public async Task HttpFailure_MapsToItsKindWithoutTheBody(string vendor, int status, InferenceFailureKind kind, string detail) {
		Connect(vendor);
		_http.Responder = _ => ((HttpStatusCode)status, """{"detail":[{"loc":["body"],"msg":"evidence text","type":"x"}],"raw":"evidence text"}""");

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal((kind, detail), (failure.Kind, failure.Detail));
	}

	[Theory]
	[InlineData("openai", 400, """{"error":{"message":"Request too large.","type":"invalid_request_error"}}""", "OpenAI rejected the decision request: Request too large.")]
	[InlineData("typesafe", 400, """{"detail":"Request exceeds 64k tokens…"}""", "TypeSafe rejected the decision request: Request exceeds 64k tokens…")]
	[InlineData("openai", 413, "not json", "OpenAI rejected the decision request.")]
	public async Task OversizedRequest_IsInputRejectedWithOnlyTheVendorMessage(string vendor, int status, string body, string detail) {
		Connect(vendor);
		_http.Responder = _ => ((HttpStatusCode)status, body);

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal((InferenceFailureKind.InputRejected, detail), (failure.Kind, failure.Detail));
	}

	[Theory]
	[MemberData(nameof(Vendors))]
	public async Task UnreachableHost_IsProviderUnavailable(string vendor) {
		Connect(vendor);
		_http.Responder = _ => throw new HttpRequestException("Name or service not known");

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal((InferenceFailureKind.ProviderUnavailable, $"Couldn't reach {Provider(vendor).Label}."), (failure.Kind, failure.Detail));
	}

	[Theory]
	[MemberData(nameof(Vendors))]
	public async Task MalformedReply_IsInvalidResponse(string vendor) {
		Connect(vendor);
		_http.Responder = _ => (HttpStatusCode.OK, "<html>");

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
	}

	[Theory]
	[MemberData(nameof(Vendors))]
	public async Task Decision_SendsTheSavedKeyAsBearerAndNeverInADetail(string vendor) {
		Connect(vendor);
		_http.Responder = _ => (HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"bad " + Key + "\"}}");

		var failure = Assert.IsType<DecisionProviderFailure>(await Decide(vendor));

		var request = Assert.Single(_http.Requests);
		Assert.Equal(HttpMethod.Post, request.Method);
		Assert.Equal(("Bearer", Key), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
		Assert.DoesNotContain(Key, failure.Detail, StringComparison.Ordinal);
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

	private Task<DecisionProviderResult> Decide(string vendor) => Provider(vendor).DecideAsync(Request(), CancellationToken.None);

	internal static DecisionProviderRequest Request() => new() {
		Owner = new InferenceOwner { Workspace = "/repo" },
		Evidence = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["review"] = "clean" }),
		Question = new DecisionQuestion("Approve the change?", [
			new DecisionChoice("approve", "The review found nothing to fix."),
			new DecisionChoice("revise", null),
		]),
		Origin = InferenceInvocationOrigin.UserInitiated,
		TimeBudget = TimeSpan.FromSeconds(60),
	};
}
