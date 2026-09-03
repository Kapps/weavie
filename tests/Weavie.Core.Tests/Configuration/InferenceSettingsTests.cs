using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class InferenceSettingsTests : IDisposable {
	private readonly string _dir = Path.Combine(
		Path.GetTempPath(),
		"weavie-inference-settings-tests",
		Guid.NewGuid().ToString("n"));

	public InferenceSettingsTests() {
		Directory.CreateDirectory(_dir);
	}

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

	private string FilePath => Path.Combine(_dir, "settings.toml");

	public void Dispose() => Directory.Delete(_dir, recursive: true);
}
