using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Xunit;

namespace Weavie.Core.Tests.Inference;

public sealed class InferenceModelDescriptionTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-inference-description-tests");
	private readonly SettingsStore _settings;

	public InferenceModelDescriptionTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
	}

	[Fact]
	public void NamesTheProviderDefaultUntilAModelIsConfigured() {
		var providers = new AgentProviderRegistry();
		providers.Register(new InferenceServiceTests.FakeProvider(new InferenceProviderSuccess {
			ModelId = "unused",
			OutputJson = "{}",
		}));
		SetString(InferenceSettings.DefaultProvider, "test-agent");

		Assert.StartsWith("Suggestions use Fake, independent", InferenceModelDescription.Describe(_settings, providers));

		SetString(InferenceSettings.Model, "opus");
		SetString(InferenceSettings.Effort, "high");

		Assert.StartsWith(
			"Suggestions use 'opus' at high effort, independent",
			InferenceModelDescription.Describe(_settings, providers));
	}

	[Fact]
	public void SaysWhyAnUnusableProviderCannotServeSuggestions() {
		SetString(InferenceSettings.DefaultProvider, "missing-agent");

		Assert.Equal(
			"Suggestions can't run: Agent provider 'missing-agent' is not registered.",
			InferenceModelDescription.Describe(_settings, new AgentProviderRegistry()));
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	private void SetString(string key, string value) =>
		_settings.Set(key, JsonSerializer.SerializeToElement(value));
}
