using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Xunit;

namespace Weavie.Core.Tests.Inference.Decisions;

public sealed class AgentDecisionProviderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-agent-decision-tests");
	private readonly SettingsStore _settings;

	public AgentDecisionProviderTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		_settings.Set(InferenceSettings.DefaultProvider, JsonSerializer.SerializeToElement("test-agent"));
	}

	[Fact]
	public async Task AsksForOneBoundedNumberPerLabel_OverTheQuestionChoicesAndEvidence() {
		var agent = Agent("{\"approve\":0.9,\"revise\":0.1}");

		var answer = Assert.IsType<DecisionAnswer>(await Decide(agent));

		Assert.Equal(("approve", 0.9), (answer.Choice, answer.Probability));
		var request = agent.LastRequest!;
		Assert.Equal((_dir.Path, InferenceModelCategory.Utility), (request.Workspace, request.Category));
		Assert.Empty(request.Images);
		using var schema = JsonDocument.Parse(request.OutputSchemaJson);
		var properties = schema.RootElement.GetProperty("properties");
		Assert.Equal(["approve", "revise"], properties.EnumerateObject().Select(p => p.Name));
		foreach (var property in properties.EnumerateObject()) {
			Assert.Equal("number", property.Value.GetProperty("type").GetString());
			Assert.Equal((0, 1), (property.Value.GetProperty("minimum").GetInt32(), property.Value.GetProperty("maximum").GetInt32()));
		}
		Assert.Equal(["approve", "revise"], schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
		Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
		using var input = JsonDocument.Parse(request.Prompt.Split("Input JSON:\n", StringSplitOptions.None)[1]);
		Assert.Equal(["question", "choices", "evidence"], input.RootElement.EnumerateObject().Select(p => p.Name));
		Assert.Equal("Is the review complete?", input.RootElement.GetProperty("question").GetString());
		var choice = input.RootElement.GetProperty("choices")[0];
		Assert.Equal(("approve", "All criteria passed."), (choice.GetProperty("label").GetString(), choice.GetProperty("description").GetString()));
		Assert.Equal("All good.", input.RootElement.GetProperty("evidence").GetProperty("summary").GetString());
	}

	[Theory]
	[InlineData(InferenceFailureKind.Refused)]
	[InlineData(InferenceFailureKind.RateLimited)]
	public async Task TypedQueryFailures_PassThrough(InferenceFailureKind kind) {
		var agent = new InferenceServiceTests.FakeProvider(new InferenceProviderFailure { ModelId = "utility", Kind = kind, Detail = "safe detail" });

		var failure = Assert.IsType<DecisionFailure>(await Decide(agent));

		Assert.Equal((kind, "safe detail", "utility"), (failure.Kind, failure.Detail, failure.Receipt!.ModelId));
	}

	[Theory]
	[InlineData("{\"approve\":0.9}")]
	[InlineData("{\"approve\":0.9,\"revise\":0.1,\"unsure\":0.1}")]
	[InlineData("{\"approve\":\"0.9\",\"revise\":0.1}")]
	[InlineData("{\"choice\":\"approve\",\"confidence\":0.9}")]
	public async Task ShapeInvalidReplies_AreInvalid(string json) {
		var failure = Assert.IsType<DecisionFailure>(await Decide(Agent(json)));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
	}

	private static InferenceServiceTests.FakeProvider Agent(string json) =>
		new(new InferenceProviderSuccess { ModelId = "utility", OutputJson = json });

	private Task<DecisionResult> Decide(InferenceServiceTests.FakeProvider agent) {
		var providers = new AgentProviderRegistry();
		providers.Register(agent);
		var service = new DecisionService(_settings, [new AgentDecisionProvider(new InferenceService(_settings, providers))]);
		return service.DecideAsync(
			new InferenceOwner { Workspace = _dir.Path },
			new DecisionServiceTests.TestEvidence("All good."),
			DecisionServiceTests.TestJson.Default.TestEvidence,
			new DecisionQuestion("Is the review complete?", [new("approve", "All criteria passed."), new("revise", null)]),
			new DecisionQueryOptions { Origin = InferenceInvocationOrigin.UserInitiated, TimeBudget = TimeSpan.FromSeconds(1) },
			CancellationToken.None);
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}
}
