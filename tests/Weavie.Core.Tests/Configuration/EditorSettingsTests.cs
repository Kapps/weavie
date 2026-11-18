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
	public void ReviewFilesAhead_DefaultsToTwo_AndIsBounded() {
		using var directory = new TempDirectory("weavie-editor-settings");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);
		static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
		long FilesAhead() {
			using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));
			return options.RootElement.GetProperty("reviewFilesAhead").GetInt64();
		}

		Assert.Equal(2, FilesAhead());
		store.Set(EditorSettings.ReviewFilesAhead, Json("0"));
		Assert.Equal(0, FilesAhead());
		Assert.Throws<SettingValidationException>(() => store.Set(EditorSettings.ReviewFilesAhead, Json("-1")));
		Assert.Throws<SettingValidationException>(() => store.Set(EditorSettings.ReviewFilesAhead, Json("7")));
	}
}
