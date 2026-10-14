using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Covers the theme payload's optional per-polarity editor slots.</summary>
public sealed class ThemeJsonTests : IDisposable {
	private readonly TempDirectory _dir = new("weavie-theme-json-tests");
	private readonly SettingsStore _settings;
	private readonly ThemeOverridesStore _overrides;

	public ThemeJsonTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_overrides = new ThemeOverridesStore(new InMemoryFileSystem(), _dir.Combine("theme-overrides.json"));
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	private JsonElement Build() => JsonDocument.Parse(ThemeJson.Build(_settings, _overrides, log: null)).RootElement;

	[Fact]
	public void Build_UnsetEditorThemes_OmitsEditorSlots() {
		var json = Build();
		Assert.False(json.TryGetProperty("editorLight", out _));
		Assert.False(json.TryGetProperty("editorDark", out _));
	}

	[Fact]
	public void Build_SetEditorTheme_WritesOnlyThatSlot() {
		_settings.Set(ThemeSettings.EditorDarkKey, JsonSerializer.SerializeToElement("weavie-light", CoreJson.Default.String));
		var json = Build();
		Assert.Equal("weavie-light", json.GetProperty("editorDark").GetProperty("id").GetString());
		Assert.Equal("weavie-dark", json.GetProperty("dark").GetProperty("id").GetString());
		Assert.False(json.TryGetProperty("editorLight", out _));
		Assert.True(ThemeSettings.IsSelectedThemeId(_settings, "weavie-light"));
	}
}
