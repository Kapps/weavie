using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Xunit;

namespace Weavie.Core.Tests.Inference;

public sealed class InferenceServiceTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-inference-tests", Guid.NewGuid().ToString("n"));
	private readonly SettingsStore _settings;

	public InferenceServiceTests() {
		Directory.CreateDirectory(_dir);
		_settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
	}

	[Fact]
	public async Task Disabled_ReturnsDisabledWithoutCallingProvider() {
		var provider = new FakeProvider(Success("{\"value\":\"unused\"}"));
		var (service, operation) = Service(provider);

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.Disabled, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task AutomaticPolicy_BlocksBeforeCallingProvider() {
		Enable();
		var provider = new FakeProvider(Success("{\"value\":\"unused\"}"));
		var (service, operation) = Service(provider);

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.Automatic,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.PolicyDenied, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task Success_SendsTypedInputAndStrictSchema_ThenReturnsTypedValue() {
		Enable();
		var provider = new FakeProvider(Success("{\"value\":\"bug/webm-fails-to-load\"}"));
		var (service, operation) = Service(provider);

		var result = Assert.IsType<InferenceSuccess<TestOutput>>(await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "WebM fails" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None));

		Assert.Equal("bug/webm-fails-to-load", result.Value.Value);
		Assert.Equal("test-agent", result.Receipt.ProviderId);
		Assert.Equal("{\"text\":\"WebM fails\"}", provider.LastRequest!.InputJson);
		using var schema = JsonDocument.Parse(provider.LastRequest.OutputSchemaJson);
		Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
		Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
		Assert.Contains("value", schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
	}

	[Fact]
	public async Task OversizedInput_IsRejectedBeforeTheProviderRuns() {
		Enable();
		var provider = new FakeProvider(Success("{\"value\":\"unused\"}"));
		var (service, operation) = Service(provider);

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = new string('x', 2048) },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.InputRejected, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task OversizedOutput_IsRejectedAfterOneProviderAttempt() {
		Enable();
		var provider = new FakeProvider(Success("{\"value\":\"" + new string('x', 2048) + "\"}"));
		var (service, operation) = Service(provider);

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.InvalidResponse, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(1, provider.Calls);
	}

	[Theory]
	[InlineData("{\"value\":\"ok\",\"extra\":true}")]
	[InlineData("{}")]
	[InlineData("{\"value\":3}")]
	[InlineData("not json")]
	public async Task ShapeInvalidJson_IsRejectedLocally(string output) {
		Enable();
		var (service, operation) = Service(new FakeProvider(Success(output)));

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.InvalidResponse, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
	}

	[Fact]
	public async Task DomainInvalidValue_IsRejectedAfterDecoding() {
		Enable();
		var (service, operation) = Service(new FakeProvider(Success("{\"value\":\"\"}")));

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		var failure = Assert.IsType<InferenceFailure<TestOutput>>(result);
		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
		Assert.Equal("value is empty", failure.Detail);
	}

	[Fact]
	public async Task ProviderFailure_IsReturnedAfterExactlyOneAttempt() {
		Enable();
		var provider = new FakeProvider(new InferenceProviderFailure {
			ModelId = "utility-model",
			Kind = InferenceFailureKind.RateLimited,
			Detail = "rate limited",
		});
		var (service, operation) = Service(provider);

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.RateLimited, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public async Task TimeBudget_CancelsOneAttemptAndReturnsTimedOut() {
		Enable();
		var provider = new FakeProvider(async ct => {
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return Success("{\"value\":\"late\"}");
		});
		var (service, operation) = Service(provider, TimeSpan.FromMilliseconds(20));

		var result = await service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None);

		Assert.Equal(InferenceFailureKind.TimedOut, Assert.IsType<InferenceFailure<TestOutput>>(result).Kind);
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public async Task CallerCancellation_PropagatesInsteadOfBecomingFallbackFailure() {
		Enable();
		var provider = new FakeProvider(async ct => {
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			return Success("{\"value\":\"late\"}");
		});
		var (service, operation) = Service(provider);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Utility,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			cancellation.Token));
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task CategoryOutsideOperationDeclaration_IsAProgrammerError() {
		var provider = new FakeProvider(Success("{\"value\":\"unused\"}"));
		var (service, operation) = Service(provider);

		await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
			operation,
			"test-agent",
			InferenceModelCategory.Reasoning,
			new TestInput { Text = "task" },
			InferenceInvocationOrigin.UserInitiated,
			CancellationToken.None));
		Assert.Equal(0, provider.Calls);
	}

	private (InferenceService Service, InferenceOperation<TestInput, TestOutput> Operation) Service(
		FakeProvider provider) => Service(provider, TimeSpan.FromSeconds(1));

	private (InferenceService Service, InferenceOperation<TestInput, TestOutput> Operation) Service(
		FakeProvider provider,
		TimeSpan timeBudget) {
		var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
			TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
			UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
			RespectRequiredConstructorParameters = true,
		};
		var operation = new InferenceOperation<TestInput, TestOutput> {
			Id = "test-operation",
			Instructions = "Return the typed result.",
			AllowedCategories = [InferenceModelCategory.Utility],
			DataKinds = InferenceDataKind.UserText,
			MaxInputBytes = 1024,
			MaxOutputBytes = 1024,
			TimeBudget = timeBudget,
			InputType = (JsonTypeInfo<TestInput>)options.GetTypeInfo(typeof(TestInput)),
			OutputType = (JsonTypeInfo<TestOutput>)options.GetTypeInfo(typeof(TestOutput)),
			Validate = static output => output.Value.Length == 0 ? "value is empty" : null,
		};
		var operations = new InferenceOperationRegistry();
		operations.Register(operation);
		var providers = new AgentProviderRegistry();
		providers.Register(provider);
		return (new InferenceService(_settings, operations, providers), operation);
	}

	private void Enable() =>
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));

	private static InferenceProviderSuccess Success(string json) => new() {
		ModelId = "utility-model",
		OutputJson = json,
	};

	public void Dispose() {
		_settings.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private sealed record TestInput {
		public required string Text { get; init; }
	}

	private sealed record TestOutput {
		public required string Value { get; init; }
	}

	private sealed class FakeProvider : IAgentInferenceProvider {
		private readonly Func<CancellationToken, Task<InferenceProviderResult>> _query;

		public FakeProvider(InferenceProviderResult result)
			: this(_ => Task.FromResult(result)) {
		}

		public FakeProvider(Func<CancellationToken, Task<InferenceProviderResult>> query) {
			_query = query;
		}

		public AgentProviderInfo Info { get; } = new() {
			Id = "test-agent",
			Name = "Test Agent",
			Capabilities = AgentProviderCapabilities.Terminal,
			Available = true,
		};

		public InferenceProviderInfo InferenceInfo { get; } = new() {
			Categories = [InferenceModelCategory.Utility],
		};

		public int Calls { get; private set; }

		public InferenceProviderRequest? LastRequest { get; private set; }

		public Task<InferenceProviderResult> QueryInferenceAsync(
			InferenceProviderRequest request,
			CancellationToken ct) {
			Calls++;
			LastRequest = request;
			return _query(ct);
		}

		public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
	}
}
