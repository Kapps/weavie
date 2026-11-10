using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

public sealed class DecisionServiceTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-decision-tests");
	private readonly SettingsStore _settings;

	public DecisionServiceTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
	}

	[Theory]
	[InlineData(false, InferenceInvocationOrigin.UserInitiated, InferenceFailureKind.Disabled)]
	[InlineData(true, InferenceInvocationOrigin.Automatic, InferenceFailureKind.PolicyDenied)]
	public async Task PolicyGates_FailWithoutCallingTheProvider(bool enabled, InferenceInvocationOrigin origin, InferenceFailureKind kind) {
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(enabled));
		var provider = Agent(0.9, 0.1);

		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide([provider], Request(_dir.Path) with { Origin = origin }));

		Assert.Equal(kind, failure.Kind);
		Assert.Equal(0, provider.Calls);
	}

	[Fact]
	public async Task TopLabel_IsChosenWithItsProbabilityAndReceipt() {
		Enable();
		var provider = Agent(0.3, 0.7);

		var success = Assert.IsType<InferenceSuccess<DecisionAnswer>>(await Decide(provider));

		Assert.Equal(("revise", 0.7, 0.3), (success.Value.Choice, success.Value.Probability, success.Value.Probabilities["approve"]));
		Assert.Equal((DecisionProviderIds.Agent, "fake-model", "req-1"),
			(success.Receipt.ProviderId, success.Receipt.ModelId, success.Receipt.RequestId));
		Assert.Same(Request(_dir.Path).Question, provider.LastRequest!.Question);
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

		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide(new FakeDecisionProvider(DecisionProviderIds.Agent, probabilities)));

		Assert.Equal((InferenceFailureKind.InvalidResponse, DecisionProviderIds.Agent), (failure.Kind, failure.Receipt!.ProviderId));
	}

	[Fact]
	public async Task ProviderFailure_PassesThroughWithReceipt() {
		Enable();

		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide(FakeDecisionProvider.Failing(InferenceFailureKind.Refused)));

		Assert.Equal((InferenceFailureKind.Refused, "safe detail", "fake-model"), (failure.Kind, failure.Detail, failure.Receipt!.ModelId));
	}

	[Fact]
	public async Task TimeBudget_CancelsTheAttemptAndReturnsTimedOut() {
		Enable();
		var provider = new FakeDecisionProvider(DecisionProviderIds.Agent, async (_, ct) => {
			await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			throw new InvalidOperationException("unreachable");
		});

		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(
			await Decide([provider], Request(_dir.Path) with { TimeBudget = TimeSpan.FromMilliseconds(20) }));

		Assert.Equal(InferenceFailureKind.TimedOut, failure.Kind);
	}

	[Fact]
	public async Task CallerCancellation_Propagates() {
		Enable();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			new DecisionService(_settings, [Agent(0.9, 0.1)]).DecideAsync(Request(_dir.Path), new CancellationToken(canceled: true)));
	}

	[Fact]
	public async Task LiveSetting_RoutesEachDecision_AndAnUnregisteredOneFailsWithoutTheAgent() {
		Enable();
		var agent = Agent(0.9, 0.1);
		var vendor = new FakeDecisionProvider(DecisionProviderIds.OpenAi, new Dictionary<string, double> { ["approve"] = 0.2, ["revise"] = 0.8 });

		var first = Assert.IsType<InferenceSuccess<DecisionAnswer>>(await Decide([agent, vendor], Request(_dir.Path)));
		Select(DecisionProviderIds.OpenAi);
		var second = Assert.IsType<InferenceSuccess<DecisionAnswer>>(await Decide([agent, vendor], Request(_dir.Path)));
		Select(DecisionProviderIds.TypeSafe);
		var third = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide([agent, vendor], Request(_dir.Path)));

		Assert.Equal(("approve", "revise"), (first.Value.Choice, second.Value.Choice));
		Assert.Equal(DecisionProviderIds.OpenAi, second.Receipt.ProviderId);
		Assert.Equal((InferenceFailureKind.NotConfigured, "Decision provider 'typesafe' is not available."), (third.Kind, third.Detail));
		Assert.Equal((1, 1), (agent.Calls, vendor.Calls));
	}

	[Fact]
	public async Task DuplicateLabels_AreAProgrammerError() {
		Enable();
		var request = Request(_dir.Path) with { Question = new("Decide.", [new("approve", null), new("approve", null)]) };

		await Assert.ThrowsAsync<ArgumentException>(() => Decide([Agent(0.9, 0.1)], request));
	}

	internal static DecisionRequest Request(string workspace) => new() {
		Owner = new InferenceOwner { Workspace = workspace },
		Evidence = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["review"] = "clean" }),
		Question = Question,
		Origin = InferenceInvocationOrigin.UserInitiated,
		TimeBudget = TimeSpan.FromSeconds(60),
	};

	private static readonly DecisionQuestion Question = new("Approve the change?",
		[new("approve", "The review found nothing to fix."), new("revise", null)]);

	private static FakeDecisionProvider Agent(double approve, double revise) =>
		new(DecisionProviderIds.Agent, new Dictionary<string, double> { ["approve"] = approve, ["revise"] = revise });

	private Task<InferenceResult<DecisionAnswer>> Decide(FakeDecisionProvider provider) => Decide([provider], Request(_dir.Path));

	private Task<InferenceResult<DecisionAnswer>> Decide(IReadOnlyList<IDecisionProvider> providers, DecisionRequest request) =>
		new DecisionService(_settings, providers).DecideAsync(request, CancellationToken.None);

	private void Enable() => _settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));

	private void Select(string id) => _settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(id));

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}
}
