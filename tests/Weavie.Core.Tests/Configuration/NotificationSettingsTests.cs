using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Tests <see cref="NotificationSettings"/>: everything-on defaults, volume/pack validation, the host JSON
/// (bare for the bootstrap global, typed for the bridge push), and core-catalog registration.
/// </summary>
[Collection("Settings")]
public sealed class NotificationSettingsTests : IDisposable {
	private readonly string _dir =
		Path.Combine(Path.GetTempPath(), "weavie-notification-tests", Guid.NewGuid().ToString("N"));

	public NotificationSettingsTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_dir, "settings.toml");

	private static SettingsRegistry Registry() {
		var registry = new SettingsRegistry();
		NotificationSettings.Register(registry);
		return registry;
	}

	private SettingsStore NewStore() =>
		new(Registry(), FilePath, enableWatcher: false, _ => Path.Combine(_dir, "ws-settings.toml"));

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

	[Fact]
	public void Defaults_EverythingOn_BundledPack() {
		using var store = NewStore();

		using var json = JsonDocument.Parse(NotificationSettings.BuildJson(store, messageType: null));
		var root = json.RootElement;
		Assert.False(root.TryGetProperty("type", out _));
		Assert.True(root.GetProperty("sounds").GetBoolean());
		Assert.True(root.GetProperty("os").GetBoolean());
		Assert.Equal(70, root.GetProperty("volume").GetInt64());
		Assert.Equal(NotificationSettings.Packs[0], root.GetProperty("soundPack").GetString());
		var gates = root.GetProperty("gates");
		Assert.True(gates.GetProperty("turnComplete").GetBoolean());
		Assert.True(gates.GetProperty("needsInput").GetBoolean());
		Assert.True(gates.GetProperty("failed").GetBoolean());
	}

	[Fact]
	public void BuildJson_ReflectsChanges_AndOptionalType() {
		using var store = NewStore();
		store.Set(NotificationSettings.Sounds, Json("false"));
		store.Set(NotificationSettings.Volume, Json("25"));
		store.Set(NotificationSettings.OnTurnComplete, Json("false"));

		using var message = JsonDocument.Parse(NotificationSettings.BuildJson(store, "notification-prefs"));
		var root = message.RootElement;
		Assert.Equal("notification-prefs", root.GetProperty("type").GetString());
		Assert.False(root.GetProperty("sounds").GetBoolean());
		Assert.Equal(25, root.GetProperty("volume").GetInt64());
		Assert.False(root.GetProperty("gates").GetProperty("turnComplete").GetBoolean());
		Assert.True(root.GetProperty("gates").GetProperty("needsInput").GetBoolean());
	}

	[Fact]
	public void Validation_RejectsOutOfRangeVolume_AndUnknownPack() {
		using var store = NewStore();

		Assert.Throws<SettingValidationException>(() => store.Set(NotificationSettings.Volume, Json("-1")));
		Assert.Throws<SettingValidationException>(() => store.Set(NotificationSettings.Volume, Json("101")));
		Assert.Throws<SettingValidationException>(() => store.Set(NotificationSettings.SoundPack, Json("\"not-a-pack\"")));

		store.Set(NotificationSettings.Volume, Json("0"));   // mute via volume is valid
		store.Set(NotificationSettings.Volume, Json("100"));
	}

	[Fact]
	public void RegisteredInCoreCatalog_WithNotificationKeys() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		using var catalog = JsonDocument.Parse(store.BuildCatalogJson());
		var keys = catalog.RootElement.GetProperty("settings")
			.EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();

		foreach (string key in NotificationSettings.Keys) {
			Assert.Contains(key, keys);
		}
	}
}
