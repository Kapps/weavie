using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class MessageSettingsTests {
	[Fact]
	public void Deadline_IsGlobalDiscoverableAndValidated() {
		using var directory = new TempDirectory("weavie-message-settings-tests");
		using var store = CoreSettings.CreateStore(directory.Combine("settings.toml"), enableWatcher: false);

		Assert.Equal(
			MessageSettings.DefaultOperationDeadlineSeconds,
			store.RequireInt(MessageSettings.OperationDeadlineSeconds));
		Assert.Throws<SettingValidationException>(() => store.Set(
			MessageSettings.OperationDeadlineSeconds,
			JsonSerializer.SerializeToElement(2)));
		store.Set(MessageSettings.OperationDeadlineSeconds, JsonSerializer.SerializeToElement(90));
		Assert.Equal(90, store.RequireInt(MessageSettings.OperationDeadlineSeconds));
	}
}
