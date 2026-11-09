using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

public sealed partial class DecisionServiceTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-decision-tests");
	private readonly SettingsStore _settings;

	public DecisionServiceTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
	}

	[Fact]
	public async Task Disabled_FailsWithoutCallingProvider() {
		var provider = Agent(0.9, 0.1);

		var failure = Assert.IsType<DecisionFailure>(await Decide(provider));

		Assert.Equal(InferenceFailureKind.Disabled, failure.Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task AutomaticPolicy_BlocksBeforeCallingProvider() {
		Enable();
		var provider = Agent(0.9, 0.1);

		var failure = Assert.IsType<DecisionFailure>(await Decide(
			Service(provider), Options(InferenceInvocationOrigin.Automatic, TimeSpan.FromSeconds(1)), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.PolicyDenied, failure.Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task TopLabel_IsChosenWithItsProbabilityAndReceipt() {
		Enable();
		var provider = Agent(0.3, 0.7);

		var answer = Assert.IsType<DecisionAnswer>(await Decide(provider));

		Assert.Equal("revise", answer.Choice);
		Assert.Equal(0.7, answer.Probability);
		Assert.Equal(0.3, answer.Probabilities["approve"]);
		Assert.Equal((DecisionProviderIds.Agent, "fake-model", "req-1"),
			(answer.Receipt.ProviderId, answer.Receipt.ModelId, answer.Receipt.RequestId));
		var request = provider.LastRequest!;
		Assert.Equal(_dir.Path, request.Owner.Workspace);
		Assert.Equal("All good.", request.Evidence.GetProperty("summary").GetString());
		Assert.Same(Question, request.Question);
		Assert.Equal(InferenceInvocationOrigin.UserInitiated, request.Origin);
	}

	public static TheoryData<Dictionary<string, double>> InvalidDistributions => [
		new Dictionary<string, double> { ["approve"] = 0.5, ["revise"] = 0.5 },
		new Dictionary<string, double> { ["approve"] = 0.9 },
		new Dictionary<string, double> { ["approve"] = 0.9, ["revise"] = 0.1, ["unsure"] = 0 },
		new Dictionary<string, double> { ["approve"] = double.NaN, ["revise"] = 0.1 },
		new Dictionary<string, double> { ["approve"] = 1.1, ["revise"] = 0.1 },
		new Dictionary<string, double> { ["approve"] = 0.9, ["revise"] = -0.1 },
	];

	[Theory]
	[MemberData(nameof(InvalidDistributions))]
	public async Task InvalidDistribution_IsRejectedWithoutRepair(Dictionary<string, double> probabilities) {
		Enable();

		var failure = Assert.IsType<DecisionFailure>(await Decide(new FakeDecisionProvider(DecisionProviderIds.Agent, probabilities)));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
		Assert.Equal(DecisionProviderIds.Agent, failure.Receipt!.ProviderId);
	}

	[Fact]
	public async Task ProviderFailure_PassesThroughWithReceipt() {
		Enable();
		var provider = new FakeDecisionProvider(DecisionProviderIds.Agent,
			(_, _) => Task.FromResult<DecisionProviderResult>(FakeDecisionProvider.Failure(InferenceFailureKind.Refused)));

		var failure = Assert.IsType<DecisionFailure>(await Decide(provider));

		Assert.Equal((InferenceFailureKind.Refused, "safe detail"), (failure.Kind, failure.Detail));
		Assert.Equal("fake-model", failure.Receipt!.ModelId);
	}

	[Fact]
	public async Task TimeBudget_CancelsTheAttemptAndReturnsTimedOut() {
		Enable();
		var provider = new FakeDecisionProvider(DecisionProviderIds.Agent, async (_, ct) => {
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			throw new InvalidOperationException("unreachable");
		});

		var failure = Assert.IsType<DecisionFailure>(await Decide(
			Service(provider), Options(InferenceInvocationOrigin.UserInitiated, TimeSpan.FromMilliseconds(20)), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.TimedOut, failure.Kind);
	}

	[Fact]
	public async Task CallerCancellation_Propagates() {
		Enable();
		using var cancelled = new CancellationTokenSource();
		cancelled.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Decide(
			Service(Agent(0.9, 0.1)), Options(InferenceInvocationOrigin.UserInitiated, TimeSpan.FromSeconds(1)), cancelled.Token));
	}

	[Fact]
	public async Task LiveSetting_RoutesEachDecision() {
		Enable();
		var agent = Agent(0.9, 0.1);
		var vendor = new FakeDecisionProvider(DecisionProviderIds.OpenAi,
			new Dictionary<string, double> { ["approve"] = 0.2, ["revise"] = 0.8 });
		var service = new DecisionService(_settings, [agent, vendor]);

		var first = Assert.IsType<DecisionAnswer>(await Decide(service, UserOptions(), CancellationToken.None));
		_settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(DecisionProviderIds.OpenAi));
		var second = Assert.IsType<DecisionAnswer>(await Decide(service, UserOptions(), CancellationToken.None));

		Assert.Equal(("approve", "revise"), (first.Choice, second.Choice));
		Assert.Equal(DecisionProviderIds.OpenAi, second.Receipt.ProviderId);
		Assert.Equal((1, 1), (agent.Calls, vendor.Calls));
	}

	[Fact]
	public async Task UnregisteredProvider_FailsVisiblyWithoutUsingTheAgent() {
		Enable();
		var agent = Agent(0.9, 0.1);
		_settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(DecisionProviderIds.TypeSafe));

		var failure = Assert.IsType<DecisionFailure>(await Decide(agent));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Contains(DecisionProviderIds.TypeSafe, failure.Detail, StringComparison.Ordinal);
		Assert.Equal(0, agent.Calls);
	}

	[Fact]
	public async Task DuplicateLabels_AreAProgrammerError() {
		Enable();
		var question = new DecisionQuestion("Decide.", [new("approve", null), new("approve", null)]);

		await Assert.ThrowsAsync<ArgumentException>(() => Service(Agent(0.9, 0.1)).DecideAsync(
			new InferenceOwner { Workspace = _dir.Path }, Evidence, TestJson.Default.TestEvidence, question, UserOptions(), CancellationToken.None));
	}

	private static readonly DecisionQuestion Question = new("Is the review complete?",
		[new("approve", "All criteria passed."), new("revise", null)]);

	private static readonly TestEvidence Evidence = new("All good.");

	private static FakeDecisionProvider Agent(double approve, double revise) =>
		new(DecisionProviderIds.Agent, new Dictionary<string, double> { ["approve"] = approve, ["revise"] = revise });

	private DecisionService Service(FakeDecisionProvider provider) => new(_settings, [provider]);

	private Task<DecisionResult> Decide(FakeDecisionProvider provider) =>
		Decide(Service(provider), UserOptions(), CancellationToken.None);

	private Task<DecisionResult> Decide(DecisionService service, DecisionQueryOptions options, CancellationToken ct) =>
		service.DecideAsync(new InferenceOwner { Workspace = _dir.Path }, Evidence, TestJson.Default.TestEvidence, Question, options, ct);

	private static DecisionQueryOptions UserOptions() => Options(InferenceInvocationOrigin.UserInitiated, TimeSpan.FromSeconds(1));

	private static DecisionQueryOptions Options(InferenceInvocationOrigin origin, TimeSpan budget) =>
		new() { Origin = origin, TimeBudget = budget };

	private void Enable() => _settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	internal sealed record TestEvidence(string Summary);

	[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
	[JsonSerializable(typeof(TestEvidence))]
	internal sealed partial class TestJson : JsonSerializerContext;
}
