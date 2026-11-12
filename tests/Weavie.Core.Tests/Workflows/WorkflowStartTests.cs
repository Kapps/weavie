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

// What a start from a prompt alone offers, and the decision that suggests one of them.
public sealed class WorkflowStartTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-workflow-start");
	private readonly SettingsStore _settings;
	private readonly WorkflowDecisionHistory _history;
	private readonly Weavie.Core.Tests.Workflows.TestRoles _roles = new();

	public WorkflowStartTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(true));
		_settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(true));
		_history = new WorkflowDecisionHistory(new LocalFileSystem(), _dir.Combine("decision-history.json"));
		Write("worktree", "small", "id: small\ndescription: A small fix.\nstages:\n  - id: a\n    run: { prompt: \"{{run.prompt}}\" }\n");
		Write("worktree", "typed", "id: typed\ndescription: Needs a url.\ninputs: { link: url }\nstages:\n  - id: a\n    run: { prompt: \"{{inputs.link}}\" }\n");
		Write("worktree", "broken", "id: broken\ndescription: Broken.\nstages:\n  - id: a\n    run: {}\n");
		Write("personal", "small", "id: small\ndescription: Shadowed.\nstages:\n  - id: a\n    run: { prompt: \"x\" }\n");
	}

	public void Dispose() {
		_roles.Dispose();
		_settings.Dispose();
		_dir.Dispose();
	}

	private void Write(string scope, string id, string text) =>
		_dir.WriteFile(scope == "personal" ? $"personal/{id}.yaml" : $"worktree/.weavie/workflows/{id}.yaml", text);

	private WorkflowCatalog Catalog => new(_dir.Combine("worktree"), _dir.Combine("personal"), WeaviePaths.RepositoryWorkflowsDir(_dir.Combine("worktree")));

	private Task<WorkflowSuggestion> Suggest(FakeDecisionProvider provider, IReadOnlyList<WorkflowDefinition> startable) =>
		new WorkflowSuggester(new DecisionService(_settings, [provider]), _history)
			.SuggestAsync(startable, "fix the typo in the header", _dir.Path, 0.8, InferenceInvocationOrigin.Automatic, CancellationToken.None);

	private static FakeDecisionProvider Answering(double none, double small) => new(DecisionProviderIds.Agent,
		new Dictionary<string, double> { [WorkflowSuggester.NoWorkflow] = none, ["small"] = small, ["quick-fix"] = 0, ["spec-driven"] = 0 });

	[Fact]
	public void APromptOffersWinnersThatDeclareNoInputsAndBrokenOnesWithTheirError() {
		var options = WorkflowStartOptions.From(Catalog, "", _roles.Resolver());

		Assert.Equal(["broken", "quick-fix", "small", "spec-driven"], options.Offered.Select(option => option.Entry.Id));
		Assert.Equal(WorkflowScope.Repository, options.Offered.Single(option => option.Entry.Id == "small").Entry.Scope);
		Assert.Contains("run needs exactly one of prompt or skill", options.Offered[0].Problem, StringComparison.Ordinal);
		Assert.Equal(["quick-fix", "small", "spec-driven"], options.Startable.Select(workflow => workflow.Id));
		Assert.Equal(["planner", "builder"], options.Offered.Single(option => option.Entry.Id == "spec-driven").Roles!.Unfilled.Select(gap => gap.Role));
		Assert.Equal((null, null), (options.Default, options.DefaultProblem));
	}

	[Theory]
	[InlineData("small", "small", null)]
	[InlineData("missing", null, "The default workflow \"missing\" isn't a workflow here.")]
	[InlineData("typed", null, "The default workflow \"typed\" declares inputs, so a prompt alone can't start it.")]
	[InlineData("broken", null, "The default workflow \"broken\" is invalid: ")]
	public void TheConfiguredDefaultIsPreselectedOnlyWhenItCanStart(string configured, string? preselected, string? problem) {
		var options = WorkflowStartOptions.From(Catalog, configured, _roles.Resolver());

		Assert.Equal(preselected, options.Default);
		if (problem is null) Assert.Null(options.DefaultProblem);
		else Assert.StartsWith(problem, options.DefaultProblem, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheSuggestionIsOneAutomaticDecisionOverTheStartableWorkflowsAndNone() {
		var provider = Answering(none: 0.1, small: 0.9);

		var suggestion = await Suggest(provider, WorkflowStartOptions.From(Catalog, "", _roles.Resolver()).Startable);

		Assert.Equal(new WorkflowSuggestion.Chosen("small"), suggestion);
		var request = provider.LastRequest!;
		Assert.Equal((_dir.Path, InferenceInvocationOrigin.Automatic), (request.Owner.Workspace, request.Origin));
		Assert.Equal("fix the typo in the header", request.Evidence.GetProperty("prompt").GetString());
		Assert.Equal([WorkflowSuggester.NoWorkflow, "quick-fix", "small", "spec-driven"], request.Question.Choices.Select(choice => choice.Label));
		Assert.Equal("A small fix.", request.Question.Choices[2].Description);
		Assert.Equal(1, _history.Summary().AgentAnswered);
	}

	[Fact]
	public async Task ExplicitSuggestionWorksWithoutAutomaticInferenceAndReportsDisabledInference() {
		_settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(false));
		var provider = Answering(none: 0.1, small: 0.9);
		var suggester = new WorkflowSuggester(new DecisionService(_settings, [provider]), _history);
		var startable = WorkflowStartOptions.From(Catalog, "", _roles.Resolver()).Startable;

		var result = await suggester.SuggestAsync(startable, "fix the typo", _dir.Path, 0.8,
			InferenceInvocationOrigin.UserInitiated, CancellationToken.None);

		Assert.Equal(new WorkflowSuggestion.Chosen("small"), result);
		Assert.Equal(InferenceInvocationOrigin.UserInitiated, provider.LastRequest!.Origin);
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(false));
		Assert.IsType<WorkflowSuggestion.Failed>(await suggester.SuggestAsync(startable, "fix the typo", _dir.Path, 0.8,
			InferenceInvocationOrigin.UserInitiated, CancellationToken.None));
		Assert.Equal(1, provider.Calls);
	}

	[Theory]
	[InlineData(0.8, 0.2, true, null)]
	[InlineData(0.2, 0.8, true, "small")]
	[InlineData(0.21, 0.79, false, null)]
	public async Task AnAnswerAppliesOnlyAtTheConfidenceItWasAskedFor(double none, double small, bool applies, string? workflow) {
		var suggestion = await Suggest(Answering(none, small), WorkflowStartOptions.From(Catalog, "", _roles.Resolver()).Startable);

		Assert.Equal(applies ? new WorkflowSuggestion.Chosen(workflow) : (WorkflowSuggestion)new WorkflowSuggestion.Unchanged(), suggestion);
	}

	[Fact]
	public async Task NothingIsAskedWhenNothingCanStartOrInferenceIsOffAndAFailureIsReported() {
		var unasked = Answering(0, 1);
		Assert.Equal(new WorkflowSuggestion.Unchanged(), await Suggest(unasked, []));
		Assert.Equal(0, unasked.Calls);

		var startable = WorkflowStartOptions.From(Catalog, "", _roles.Resolver()).Startable;
		Assert.Equal(new WorkflowSuggestion.Failed("safe detail"), await Suggest(FakeDecisionProvider.Failing(InferenceFailureKind.TimedOut), startable));

		_settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(false));
		Assert.Equal(new WorkflowSuggestion.Unchanged(), await Suggest(unasked, startable));
		_settings.Set(InferenceSettings.Enabled, JsonSerializer.SerializeToElement(false));
		Assert.Equal(new WorkflowSuggestion.Unchanged(), await Suggest(unasked, startable));
		Assert.Equal(0, unasked.Calls);
	}

	[Fact]
	public void TheChosenProviderDecidesWhatCanStartAndWhichRolesNeedAModel() {
		_roles.Offer("alpha", "beta");
		_roles.Save("planner", "alpha");
		_roles.Save("builder", "gone");

		var spec = WorkflowStartOptions.From(Catalog, "spec-driven", _roles.Resolver()).Offered.Single(option => option.Entry.Id == "spec-driven");

		Assert.Null(spec.Problem);
		Assert.Equal([new WorkflowRoleGap("builder", "gone")], spec.Roles!.Unfilled);
		var terminal = _roles.Provider.Info with { Name = "Claude Code", Capabilities = Weavie.Core.Agents.AgentProviderCapabilities.Terminal };
		var options = WorkflowStartOptions.From(Catalog, "spec-driven", _roles.Resolver(terminal));
		Assert.Null(options.Default);
		Assert.StartsWith("The default workflow \"spec-driven\" can't run here: Stage \"requirements\" runs on role \"planner\", but Claude Code runs in a terminal", options.DefaultProblem, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ARoleSuggestionIsOneDecisionOverTheProvidersModelsWithItsStagesAsEvidence() {
		var definition = Catalog.List().Single(entry => entry.Id == "spec-driven").Definition!;
		var provider = new FakeDecisionProvider(DecisionProviderIds.Agent, new Dictionary<string, double> { ["alpha"] = 0.9, ["beta"] = 0.1 });
		var suggester = new WorkflowSuggester(new DecisionService(_settings, [provider]), _history);
		IReadOnlyList<Weavie.Core.Agents.AgentControlOption> models = [
			new() { Id = "alpha", Label = "Alpha", Description = "For hard work" }, new() { Id = "beta", Label = "Beta" }];

		var suggestion = await suggester.SuggestRoleAsync(definition, "builder", models, _dir.Path, 0.8, CancellationToken.None);

		Assert.Equal(new WorkflowSuggestion.Chosen("alpha"), suggestion);
		var request = provider.LastRequest!;
		Assert.Equal(("builder", 1), (request.Evidence.GetProperty("role").GetString(), request.Evidence.GetProperty("prompts").GetArrayLength()));
		Assert.Equal(["Alpha: For hard work", "Beta"], request.Question.Choices.Select(choice => choice.Description));
		Assert.Equal(new WorkflowSuggestion.Unchanged(), await suggester.SuggestRoleAsync(definition, "builder", [models[0]], _dir.Path, 0.8, CancellationToken.None));
		Assert.Equal(new WorkflowSuggestion.Unchanged(), await suggester.SuggestRoleAsync(definition, "builder", null, _dir.Path, 0.8, CancellationToken.None));
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public void NoWorkflowCanNeverBeAWorkflowId() {
		Write("worktree", WorkflowSuggester.NoWorkflow, $"id: {WorkflowSuggester.NoWorkflow}\ndescription: d\nstages:\n  - id: a\n    run: {{ prompt: \"x\" }}\n");

		Assert.Null(Catalog.List().Single(entry => entry.Id == WorkflowSuggester.NoWorkflow).Definition);
	}
}
