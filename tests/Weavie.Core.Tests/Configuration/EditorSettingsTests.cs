using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests.Configuration;

public sealed class EditorSettingsTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), $"weavie-editor-settings-{Guid.NewGuid():N}");

	[Fact]
	public void DefaultWheelSensitivityLeavesPlatformNormalizationToClient() {
		Directory.CreateDirectory(_directory);
		using var store = CoreSettings.CreateStore(Path.Combine(_directory, "settings.toml"), enableWatcher: false);
		using var options = JsonDocument.Parse(EditorSettings.BuildJson(store));

		Assert.Equal(1, options.RootElement.GetProperty("mouseWheelScrollSensitivity").GetInt64());
	}

	public void Dispose() {
		if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
	}
}
