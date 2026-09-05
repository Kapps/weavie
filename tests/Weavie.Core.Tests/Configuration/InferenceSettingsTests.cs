using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class InferenceSettingsTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-inference-settings-tests");

	[Fact]
	public void DefaultsSelectClaudeAndInheritItsProfile() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.Equal("claude", store.RequireString(InferenceSettings.DefaultProvider));
		Assert.Equal(string.Empty, store.RequireString(InferenceSettings.Model));
		Assert.Equal(string.Empty, store.RequireString(InferenceSettings.Effort));
		Assert.Equal("inherit", store.RequireString(InferenceSettings.FastMode));
	}

	[Fact]
	public void FileCanSelectAnExactProviderProfile() {
		File.WriteAllText(FilePath, """
			inference.defaultProvider = "codex-acp"
			inference.model = "opus"
			inference.effort = "low"
			inference.fastMode = "on"
			""");
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		Assert.Equal("codex-acp", store.RequireString(InferenceSettings.DefaultProvider));
		Assert.Equal("opus", store.RequireString(InferenceSettings.Model));
		Assert.Equal("low", store.RequireString(InferenceSettings.Effort));
		Assert.Equal("on", store.RequireString(InferenceSettings.FastMode));
	}

	private string FilePath => _dir.Combine("settings.toml");

	public void Dispose() => _dir.Dispose();
}
