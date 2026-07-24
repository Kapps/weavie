using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Spelling;
using Xunit;

namespace Weavie.Core.Tests;

[Collection("Settings")]
public sealed class SpellSettingsTests : IDisposable {
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "weavie-spell-settings-tests", Guid.NewGuid().ToString("N"));

	public SpellSettingsTests() {
		Directory.CreateDirectory(_directory);
	}

	public void Dispose() {
		try {
			Directory.Delete(_directory, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	private string FilePath => Path.Combine(_directory, "settings.toml");

	private SettingsStore NewStore() {
		var registry = new SettingsRegistry();
		SpellSettings.Register(registry);
		return new SettingsStore(registry, FilePath, enableWatcher: false, _ => Path.Combine(_directory, "workspace.toml"));
	}

	[Fact]
	public void DefaultsAndJson_ExposeEnabledAmericanEnglish() {
		using var store = NewStore();

		using var document = JsonDocument.Parse(SpellSettings.BuildJson(store, "spell-settings"));
		var root = document.RootElement;
		Assert.Equal("spell-settings", root.GetProperty("type").GetString());
		Assert.True(root.GetProperty("enabled").GetBoolean());
		Assert.Equal(SpellLocales.EnUs, root.GetProperty("locale").GetString());
	}

	[Fact]
	public void Locale_OnlyAcceptsEmbeddedLocales() {
		using var store = NewStore();
		store.Set(SpellSettings.Locale, Json("\"en-GB\""));

		Assert.Equal(SpellLocales.EnGb, store.RequireString(SpellSettings.Locale));
		Assert.Throws<SettingValidationException>(() => store.Set(SpellSettings.Locale, Json("\"fr-FR\"")));
	}

	[Fact]
	public void CoreCatalog_ContainsSpellSettings() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);
		using var document = JsonDocument.Parse(store.BuildCatalogJson());
		var keys = document.RootElement.GetProperty("settings")
			.EnumerateArray().Select(setting => setting.GetProperty("key").GetString()).ToList();

		Assert.Contains(SpellSettings.Enabled, keys);
		Assert.Contains(SpellSettings.Locale, keys);
	}

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
