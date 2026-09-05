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
}
