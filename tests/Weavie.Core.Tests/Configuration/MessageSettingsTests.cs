using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class MessageSettingsTests : IDisposable {
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"weavie-message-settings-tests",
		Guid.NewGuid().ToString("N"));

	[Fact]
	public void Deadline_IsGlobalDiscoverableAndValidated() {
		Directory.CreateDirectory(_directory);
		using var store = CoreSettings.CreateStore(Path.Combine(_directory, "settings.toml"), enableWatcher: false);

		Assert.Equal(
			MessageSettings.DefaultOperationDeadlineSeconds,
			store.RequireInt(MessageSettings.OperationDeadlineSeconds));
		Assert.Throws<SettingValidationException>(() => store.Set(
			MessageSettings.OperationDeadlineSeconds,
			JsonSerializer.SerializeToElement(2)));
		store.Set(MessageSettings.OperationDeadlineSeconds, JsonSerializer.SerializeToElement(90));
		Assert.Equal(90, store.RequireInt(MessageSettings.OperationDeadlineSeconds));
	}

	public void Dispose() {
		if (Directory.Exists(_directory)) {
			Directory.Delete(_directory, recursive: true);
		}
	}
}
