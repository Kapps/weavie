using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests.Configuration;

public sealed class EditorSettingsTests {
	[Fact]
	public void DefaultWheelSensitivityLeavesPlatformNormalizationToClient() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));

		Assert.Equal(1, options.RootElement.GetProperty("mouseWheelScrollSensitivity").GetInt64());
	}

	[Fact]
	public void ReviewScreensAhead_IsInTheOptionsPayload() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		store.Set(EditorSettings.ReviewScreensAhead, JsonDocument.Parse("4").RootElement.Clone());
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));

		Assert.Equal(4, options.RootElement.GetProperty("reviewScreensAhead").GetInt64());
	}

	[Fact]
	public void InlineReplies_IsInTheOptionsPayload() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		store.Set(EditorSettings.InlineReplies, JsonDocument.Parse("false").RootElement.Clone());
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));

		Assert.False(options.RootElement.GetProperty("inlineReplies").GetBoolean());
	}
}
