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
	public void ReviewFilesAhead_IsInTheOptionsPayload() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		store.Set(EditorSettings.ReviewFilesAhead, JsonDocument.Parse("4").RootElement.Clone());
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));

		Assert.Equal(4, options.RootElement.GetProperty("reviewFilesAhead").GetInt64());
	}

	[Fact]
	public void InlineReplies_AreOnUntilTurnedOff() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		Assert.True(InlineReplies(store));

		store.Set(EditorSettings.InlineReplies, JsonDocument.Parse("false").RootElement.Clone());

		Assert.False(InlineReplies(store));
	}

	private static bool InlineReplies(SettingsStore store) {
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));
		return options.RootElement.GetProperty("inlineReplies").GetBoolean();
	}
}
