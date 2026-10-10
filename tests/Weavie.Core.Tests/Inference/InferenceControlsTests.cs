using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Xunit;

namespace Weavie.Core.Tests.Inference;

public sealed class InferenceControlsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-inference-controls-tests");
	private readonly SettingsStore _settings;
	private readonly AgentProviderRegistry _providers = new();
	private readonly ProbingProvider _provider = new();

	public InferenceControlsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_providers.Register(_provider);
		Set(InferenceSettings.DefaultProvider, "test-agent");
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public void DefaultsLeadEachAxisAndResolveToTheProvidersChoice() {
		var state = Build(Controls("haiku", "low", fast: false));

		Assert.Equal(
			[InferenceSettings.DefaultProvider, InferenceSettings.Model, InferenceSettings.Effort, InferenceSettings.FastMode],
			state.Axes.Select(axis => axis.Id));
		var model = Axis(state, InferenceSettings.Model);
		Assert.Equal(new AgentControlOption { Id = "", Label = "Default (Haiku)" }, model.Options[0]);
		Assert.Equal("Haiku", model.ValueLabel);
		Assert.Equal(["", "haiku", "opus"], model.Options.Select(option => option.Id));
		var fast = Axis(state, InferenceSettings.FastMode);
		Assert.Equal(["inherit", "on", "off"], fast.Options.Select(option => option.Id));
		Assert.Equal("Off", fast.ValueLabel);
		Assert.Null(state.Warning);
	}

	[Fact]
	public void AnExplicitValueShowsItsOwnLabel() {
		Set(InferenceSettings.Model, "opus");

		var model = Axis(Build(Controls("haiku", "low", fast: false)), InferenceSettings.Model);

		Assert.Equal("opus", model.Value);
		Assert.Equal("Opus", model.ValueLabel);
	}

	[Fact]
	public void AnUnofferedValueStaysSelectedAndWarns() {
		Set(InferenceSettings.Model, "gpt-9");

		var state = Build(Controls("haiku", "low", fast: false));

		var model = Axis(state, InferenceSettings.Model);
		Assert.Equal("gpt-9", model.ValueLabel);
		Assert.Equal("gpt-9", model.Options[^1].Id);
		Assert.Equal("Model 'gpt-9' isn't offered by Test Agent.", state.Warning);
	}

	[Fact]
	public void AxesTheProviderLacksAreOmittedUntilTheyAreConfigured() {
		var controls = new InferenceControls { Defaults = [], Selected = [] };

		Assert.Equal([InferenceSettings.DefaultProvider], Build(controls).Axes.Select(axis => axis.Id));

		Set(InferenceSettings.FastMode, "on");
		var state = Build(controls);
		Assert.Equal("Fast mode 'on' isn't offered by Test Agent.", state.Warning);
		// The default stays pickable, so an unoffered value can always be cleared from the picker.
		Assert.Equal(["inherit", "on"], Axis(state, InferenceSettings.FastMode).Options.Select(option => option.Id));
	}

	[Fact]
	public async Task CatalogAsksOnlyOnceOpenedAndAgainWhenTheModelChanges() {
		using var catalog = new InferenceControlCatalog(_settings, _providers);
		Set(InferenceSettings.Model, "haiku");
		Assert.Empty(_provider.Asked);

		Assert.Equal(InferenceControlsStatus.Probing, catalog.Open().Status);
		_provider.Answer(Controls("haiku", "low", fast: false));
		await WaitFor(() => catalog.State.Status == InferenceControlsStatus.Ready);

		Set(InferenceSettings.Model, "opus");
		Set(InferenceSettings.Effort, "high");

		Assert.Equal(["haiku", "opus"], _provider.Asked);
	}

	[Fact]
	public async Task ASupersededAnswerIsDropped() {
		using var catalog = new InferenceControlCatalog(_settings, _providers);
		catalog.Open();
		var first = _provider.Pending;

		catalog.Refresh();
		first.SetResult(Controls("stale", "low", fast: false));
		_provider.Answer(Controls("haiku", "low", fast: false));
		await WaitFor(() => catalog.State.Status == InferenceControlsStatus.Ready);

		Assert.Equal("Haiku", Axis(catalog.State, InferenceSettings.Model).ValueLabel);
	}

	[Fact]
	public async Task AFailedAskSaysWhy() {
		using var catalog = new InferenceControlCatalog(_settings, _providers);
		catalog.Open();

		_provider.Pending.SetException(new InvalidOperationException("Sign in first."));
		await WaitFor(() => catalog.State.Status == InferenceControlsStatus.Failed);

		Assert.Equal("Sign in first.", catalog.State.Error);
	}

	private InferenceControlsState Build(InferenceControls controls) =>
		InferenceControlAxes.Build(_settings, _providers, InferenceControlsStatus.Ready, null, controls);

	private static AgentControlAxis Axis(InferenceControlsState state, string key) =>
		state.Axes.Single(axis => axis.Id == key);

	private void Set(string key, string value) => _settings.Set(key, JsonSerializer.SerializeToElement(value));

	private static async Task WaitFor(Func<bool> condition) {
		for (int attempt = 0; !condition(); attempt++) {
			Assert.True(attempt < 500, "The catalog never reached the expected state.");
			await Task.Delay(10);
		}
	}

	private static InferenceControls Controls(string model, string effort, bool fast) {
		AgentControlAxis[] axes = [
			Select("model", "model", model, [("haiku", "Haiku"), ("opus", "Opus"), ("stale", "Stale")]),
			Select("effort", "thought_level", effort, [("low", "Low"), ("high", "High")]),
			new() {
				Id = "fast",
				Label = "Fast",
				Kind = "boolean",
				Value = fast ? "true" : "false",
				ValueLabel = fast ? "On" : "Off",
				Options = [new() { Id = "true", Label = "On" }, new() { Id = "false", Label = "Off" }],
			},
		];
		return new InferenceControls { Defaults = axes, Selected = axes };
	}

	private static AgentControlAxis Select(string id, string category, string value, (string Id, string Label)[] options) => new() {
		Id = id,
		Label = id,
		Category = category,
		Kind = "select",
		Value = value,
		ValueLabel = options.First(option => option.Id == value).Label,
		Options = [.. options.Where(option => option.Id != "stale" || value == "stale").Select(option => new AgentControlOption { Id = option.Id, Label = option.Label })],
	};

	private sealed class ProbingProvider : IAgentInferenceProvider {
		public List<string> Asked { get; } = [];

		public TaskCompletionSource<InferenceControls> Pending { get; private set; } = new();

		public void Answer(InferenceControls controls) => Pending.SetResult(controls);

		public AgentProviderInfo Info { get; } = new() {
			Id = "test-agent",
			Name = "Test Agent",
			Capabilities = AgentProviderCapabilities.Terminal,
			Available = true,
		};

		public InferenceProviderInfo InferenceInfo { get; } = new() {
			Categories = [InferenceModelCategory.Utility],
			UtilityModel = "Fake",
			UtilityEffort = "",
		};

		public Task<InferenceControls> ProbeInferenceControlsAsync(string model, CancellationToken ct) {
			Asked.Add(model);
			Pending = new TaskCompletionSource<InferenceControls>(TaskCreationOptions.RunContinuationsAsynchronously);
			return Pending.Task;
		}

		public Task<InferenceProviderResult> QueryInferenceAsync(InferenceProviderRequest request, CancellationToken ct) =>
			throw new NotSupportedException();

		public void ClearConversation(string workspace) { }

		public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
	}
}
