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

	public InferenceControlsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_providers.Register(new InferenceServiceTests.FakeProvider(new InferenceProviderSuccess { ModelId = "m", OutputJson = "{}" }));
		_settings.Set(InferenceSettings.DefaultProvider, JsonSerializer.SerializeToElement("test-agent"));
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public void EachAxisLeadsWithTheDefaultTheAgentResolvesTo() {
		var choices = Build(Controls(fast: true));

		Assert.Equal(
			[InferenceSettings.DefaultProvider, InferenceSettings.Model, InferenceSettings.Effort, InferenceSettings.FastMode],
			choices.Axes.Select(axis => axis.Id));
		var model = choices.Axes[1];
		Assert.Equal(["Default (Haiku)", "Haiku", "Opus"], model.Options.Select(option => option.Label));
		Assert.Equal("Haiku", model.ValueLabel);
		Assert.Equal(["inherit", "on", "off"], choices.Axes[3].Options.Select(option => option.Id));
		Assert.Null(choices.Warning);
	}

	[Fact]
	public void AnUnofferedValueStaysSelectedAndClearableWithAWarning() {
		_settings.Set(InferenceSettings.FastMode, JsonSerializer.SerializeToElement("on"));

		var choices = Build(Controls(fast: false));

		var fast = choices.Axes.Single(axis => axis.Id == InferenceSettings.FastMode);
		Assert.Equal(["inherit", "on"], fast.Options.Select(option => option.Id));
		Assert.Equal("Fast mode 'on' isn't offered by Test Agent.", choices.Warning);
	}

	private InferenceChoices Build(InferenceControls controls) => InferenceControlAxes.Build(_settings, _providers, controls);

	private static InferenceControls Controls(bool fast) {
		var model = new AgentControlAxis {
			Id = "model",
			Label = "Model",
			Category = "model",
			Kind = "select",
			Value = "haiku",
			ValueLabel = "Haiku",
			Options = [new() { Id = "haiku", Label = "Haiku" }, new() { Id = "opus", Label = "Opus" }],
		};
		var effort = model with { Id = "effort", Category = "thought_level", Value = "low", ValueLabel = "Low", Options = [new() { Id = "low", Label = "Low" }] };
		AgentControlAxis[] axes = fast
			? [model, effort, model with { Id = "fast", Category = null, Kind = "boolean", Value = "false", ValueLabel = "Off", Options = [] }]
			: [model, effort];
		return new InferenceControls { Defaults = axes, Selected = axes };
	}
}
