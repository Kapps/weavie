using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Tests.Inference;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class InferenceWorkflowDeciderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-decision-adapter");
	private static WorkflowDecisionRequest Request() => new("Is the review complete?",
		[new("approve", "All criteria passed."), new("revise", null)],
		new WorkflowDecisionEvidence(new Dictionary<string, string> { ["review"] = "No issues." }, "Reviewed.", [new("review.md", "Complete contents: café 世界\n[link](unread.md)\n")]));

	[Theory]
	[InlineData(0.79)]
	[InlineData(0.91)]
	public async Task SendsPreparedEvidenceAndDescriptionsWithoutPolicyAndPreservesConfidence(double confidence) {
		var provider = new InferenceServiceTests.FakeProvider(new InferenceProviderSuccess {
			ModelId = "utility",
			OutputJson = FormattableString.Invariant($"{{\"choice\":\"approve\",\"confidence\":{confidence}}}"),
		});
		var result = await Decide(provider);
		Assert.Equal(new WorkflowDecisionResult.Answered("approve", confidence), result);
		Assert.Equal(1, provider.Calls);
		var request = provider.LastRequest!;
		Assert.Equal(_dir.Path, request.Workspace);
		Assert.Equal(InferenceModelCategory.Utility, request.Category);
		Assert.Empty(request.Images);
		using var json = JsonDocument.Parse(request.Prompt.Split("Input JSON:\n", StringSplitOptions.None)[1]);
		Assert.Equal(["question", "choices", "evidence"], json.RootElement.EnumerateObject().Select(p => p.Name));
		Assert.Equal("All criteria passed.", json.RootElement.GetProperty("choices")[0].GetProperty("description").GetString());
		Assert.Equal("No issues.", json.RootElement.GetProperty("evidence").GetProperty("outputs").GetProperty("review").GetString());
		var file = Assert.Single(json.RootElement.GetProperty("evidence").GetProperty("files").EnumerateArray());
		Assert.Equal("review.md", file.GetProperty("path").GetString());
		Assert.Equal(Request().Evidence.Files[0].Text, file.GetProperty("text").GetString());
		Assert.DoesNotContain("unsure", request.Prompt, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("{\"choice\":\"approve\"}")]
	[InlineData("{\"choice\":\"approve\",\"confidence\":\"0.9\"}")]
	[InlineData("{\"choice\":\"approve\",\"confidence\":null}")]
	[InlineData("{\"choice\":\"approve\",\"confidence\":0.9,\"reason\":\"prose\"}")]
	public async Task StrictNumericResponseIsRequired(string json) {
		var provider = new InferenceServiceTests.FakeProvider(new InferenceProviderSuccess { ModelId = "utility", OutputJson = json });
		var failure = Assert.IsType<WorkflowDecisionResult.Failed>(await Decide(provider));
		Assert.StartsWith("InvalidResponse:", failure.Reason, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(InferenceFailureKind.Refused)]
	[InlineData(InferenceFailureKind.ProviderUnavailable)]
	public async Task PreservesRefusalAndSafeFailures(InferenceFailureKind kind) {
		var provider = new InferenceServiceTests.FakeProvider(new InferenceProviderFailure { ModelId = "utility", Kind = kind, Detail = "safe detail" });
		var result = await Decide(provider);
		if (kind == InferenceFailureKind.Refused) Assert.Equal(new WorkflowDecisionResult.Refused("safe detail"), result);
		else Assert.Equal(new WorkflowDecisionResult.Failed("ProviderUnavailable: safe detail"), result);
	}

	private async Task<WorkflowDecisionResult> Decide(InferenceServiceTests.FakeProvider provider) {
		using var settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		settings.Set(InferenceSettings.DefaultProvider, JsonSerializer.SerializeToElement("test-agent"));
		var providers = new AgentProviderRegistry();
		providers.Register(provider);
		return await new InferenceWorkflowDecider(new InferenceService(settings, providers), _dir.Path)
			.DecideAsync(Request(), CancellationToken.None);
	}

	public void Dispose() => _dir.Dispose();
}
