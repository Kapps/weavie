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

		var success = Assert.IsType<InferenceSuccess<DecisionAnswer>>(await Decide(agent));

		Assert.Equal(("approve", 0.9), (success.Value.Choice, success.Value.Probability));
		var request = agent.LastRequest!;
		Assert.Equal((_dir.Path, InferenceModelCategory.Utility), (request.Workspace, request.Category));
		Assert.Empty(request.Images);
		Assert.Equal(
			"""{"type":"object","properties":{"approve":{"type":"number","minimum":0,"maximum":1},"revise":{"type":"number","minimum":0,"maximum":1}},"required":["approve","revise"],"additionalProperties":false}""",
			request.OutputSchemaJson);
		using var input = JsonDocument.Parse(request.Prompt.Split("Input JSON:\n", StringSplitOptions.None)[1]);
		Assert.Equal(["question", "choices", "evidence"], input.RootElement.EnumerateObject().Select(p => p.Name));
		Assert.Equal("Approve the change?", input.RootElement.GetProperty("question").GetString());
		var choice = input.RootElement.GetProperty("choices")[0];
		Assert.Equal(("approve", "The review found nothing to fix."), (choice.GetProperty("label").GetString(), choice.GetProperty("description").GetString()));
		Assert.Equal("clean", input.RootElement.GetProperty("evidence").GetProperty("review").GetString());
	}

	[Fact]
	public async Task TypedQueryFailures_PassThrough() {
		var agent = new InferenceServiceTests.FakeProvider(
			new InferenceProviderFailure { ModelId = "utility", Kind = InferenceFailureKind.Refused, Detail = "safe detail" });

		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide(agent));

		Assert.Equal((InferenceFailureKind.Refused, "safe detail", "utility"), (failure.Kind, failure.Detail, failure.Receipt!.ModelId));
	}

	[Theory]
	[InlineData("{\"approve\":\"0.9\",\"revise\":0.1}")]
	[InlineData("{\"choice\":\"approve\",\"confidence\":0.9}")]
	public async Task ShapeInvalidReplies_AreInvalid(string json) {
		var failure = Assert.IsType<InferenceFailure<DecisionAnswer>>(await Decide(Agent(json)));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
	}

	private static InferenceServiceTests.FakeProvider Agent(string json) =>
		new(new InferenceProviderSuccess { ModelId = "utility", OutputJson = json });

	private Task<InferenceResult<DecisionAnswer>> Decide(InferenceServiceTests.FakeProvider agent) {
		var providers = new AgentProviderRegistry();
		providers.Register(agent);
		var service = new DecisionService(_settings, [new AgentDecisionProvider(new InferenceService(_settings, providers))]);
		return service.DecideAsync(DecisionServiceTests.Request(_dir.Path), CancellationToken.None);
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}
}
