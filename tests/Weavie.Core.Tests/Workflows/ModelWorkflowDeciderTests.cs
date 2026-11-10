using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Inference.Decisions;
using Weavie.Core.Tests.Inference.Decisions;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ModelWorkflowDeciderTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-model-decider");
	private readonly SettingsStore _settings;
	private readonly WorkflowDecisionHistory _history;

	public ModelWorkflowDeciderTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		_history = new WorkflowDecisionHistory(new LocalFileSystem(), _dir.Combine("decision-history.json"));
	}

	private static WorkflowDecisionRequest Request() => new("Is the review complete?",
		[new("approve", "All criteria passed."), new("revise", null)],
		new WorkflowDecisionEvidence(new Dictionary<string, string> { ["review"] = "No issues." }, "Reviewed.", [new("review.md", "Complete contents: café 世界\n")]));

	[Fact]
	public async Task AnswerCarriesTheChosenProbabilityAndIsRecorded() {
		var provider = new FakeDecisionProvider(DecisionProviderIds.Agent,
			new Dictionary<string, double> { ["approve"] = 0.79, ["revise"] = 0.21 });

		var result = await Decide(provider);

		Assert.Equal(new WorkflowDecisionResult.Answered("approve", 0.79), result);
		var request = provider.LastRequest!;
		Assert.Equal(_dir.Path, request.Owner.Workspace);
		Assert.Equal(("Is the review complete?", InferenceInvocationOrigin.UserInitiated), (request.Question.Instructions, request.Origin));
		Assert.Equal([new("approve", "All criteria passed."), new DecisionChoice("revise", null)], request.Question.Choices);
		Assert.Equal("No issues.", request.Evidence.GetProperty("outputs").GetProperty("review").GetString());
		Assert.Equal("Reviewed.", request.Evidence.GetProperty("summary").GetString());
		var file = Assert.Single(request.Evidence.GetProperty("files").EnumerateArray());
		Assert.Equal(("review.md", Request().Evidence.Files[0].Text), (file.GetProperty("path").GetString(), file.GetProperty("text").GetString()));
		Assert.Equal(1, _history.Summary().AgentAnswered);
	}

	[Theory]
	[InlineData(InferenceFailureKind.Refused)]
	[InlineData(InferenceFailureKind.ProviderUnavailable)]
	public async Task RefusalsAndFailuresAreReportedAndNotRecorded(InferenceFailureKind kind) {
		var result = await Decide(FakeDecisionProvider.Failing(kind));

		Assert.Equal(kind == InferenceFailureKind.Refused
			? new WorkflowDecisionResult.Refused("safe detail")
			: new WorkflowDecisionResult.Failed("ProviderUnavailable: safe detail"), result);
		Assert.Equal(0, _history.Summary().AgentAnswered);
	}

	private Task<WorkflowDecisionResult> Decide(FakeDecisionProvider provider) =>
		new ModelWorkflowDecider(new DecisionService(_settings, [provider]), _dir.Path, _history)
			.DecideAsync(Request(), CancellationToken.None);

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}
}
