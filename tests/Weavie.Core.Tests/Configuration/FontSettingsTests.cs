using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="FontSettings"/>: the global default, per-surface override precedence, the
/// inherit sentinels (empty family, <c>0</c> size, <c>"inherit"</c> weight), validation, and the JSON
/// the host injects/pushes. Uses an isolated registry (font settings only) over a temp file.
/// </summary>
[Collection("Settings")]
public sealed class FontSettingsTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-font-tests", Guid.NewGuid().ToString("N"));

	public FontSettingsTests() {
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
		FontSettings.Register(registry);
		return registry;
	}

	private SettingsStore NewStore() => new(Registry(), FilePath, enableWatcher: false);

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

	[Fact]
	public void Defaults_BothSurfacesInheritTheGlobal() {
		using var store = NewStore();

		var editor = FontSettings.ResolveEditor(store);
		var terminal = FontSettings.ResolveTerminal(store);

		Assert.Equal(13, editor.Size);
		Assert.Equal(13, terminal.Size);
		Assert.Equal("normal", editor.Weight);
		Assert.Equal("normal", terminal.Weight);
		Assert.Contains("monospace", editor.Family, StringComparison.Ordinal);
		Assert.Equal(editor.Family, terminal.Family);
	}

	[Fact]
	public void Override_WinsForItsSurfaceOnly() {
		using var store = NewStore();
		store.Set(FontSettings.EditorSize, Json("18"));
		store.Set(FontSettings.EditorFamily, Json("\"JetBrains Mono\""));

		var editor = FontSettings.ResolveEditor(store);
		var terminal = FontSettings.ResolveTerminal(store);

		Assert.Equal(18, editor.Size);
		Assert.Equal("JetBrains Mono", editor.Family);
		// Terminal is untouched — still the global default.
		Assert.Equal(13, terminal.Size);
		Assert.Contains("monospace", terminal.Family, StringComparison.Ordinal);
	}

	[Fact]
	public void GlobalChange_PropagatesToBoth_WhenNotOverridden() {
		using var store = NewStore();
		store.Set(FontSettings.GlobalSize, Json("20"));
		store.Set(FontSettings.GlobalWeight, Json("\"bold\""));

		var editor = FontSettings.ResolveEditor(store);
		var terminal = FontSettings.ResolveTerminal(store);

		Assert.Equal(20, editor.Size);
		Assert.Equal(20, terminal.Size);
		Assert.Equal("bold", editor.Weight);
		Assert.Equal("bold", terminal.Weight);
	}

	[Fact]
	public void InheritSentinels_FallThroughToGlobal() {
		using var store = NewStore();
		store.Set(FontSettings.GlobalSize, Json("16"));
		store.Set(FontSettings.GlobalWeight, Json("\"600\""));
		store.Set(FontSettings.GlobalFamily, Json("\"Comic Mono\""));
		// Explicit inherit sentinels on the editor: empty family, 0 size, "inherit" weight.
		store.Set(FontSettings.EditorFamily, Json("\"\""));
		store.Set(FontSettings.EditorSize, Json("0"));
		store.Set(FontSettings.EditorWeight, Json("\"inherit\""));

		var editor = FontSettings.ResolveEditor(store);

		Assert.Equal(16, editor.Size);
		Assert.Equal("600", editor.Weight);
		Assert.Equal("Comic Mono", editor.Family);
	}

	[Fact]
	public void Validation_RejectsOutOfRangeSize_AndBadWeight() {
		using var store = NewStore();

		Assert.Throws<SettingValidationException>(() => store.Set(FontSettings.GlobalSize, Json("0")));   // below min
		Assert.Throws<SettingValidationException>(() => store.Set(FontSettings.GlobalSize, Json("999"))); // above max
		Assert.Throws<SettingValidationException>(() => store.Set(FontSettings.GlobalWeight, Json("\"heavy\"")));
		Assert.Throws<SettingValidationException>(() => store.Set(FontSettings.EditorWeight, Json("\"heavy\"")));

		// Override size 0 (inherit) is allowed; a real out-of-range override is not.
		store.Set(FontSettings.EditorSize, Json("0"));
		Assert.Throws<SettingValidationException>(() => store.Set(FontSettings.EditorSize, Json("3")));
	}

	[Fact]
	public void BuildJson_HasBothSurfaces_AndOptionalType() {
		using var store = NewStore();
		store.Set(FontSettings.TerminalSize, Json("11"));

		using var bare = JsonDocument.Parse(FontSettings.BuildJson(store, messageType: null));
		Assert.False(bare.RootElement.TryGetProperty("type", out _));
		var editor = bare.RootElement.GetProperty("editor");
		Assert.Equal(13, editor.GetProperty("size").GetInt32());
		Assert.Equal("normal", editor.GetProperty("weight").GetString());
		Assert.False(string.IsNullOrEmpty(editor.GetProperty("family").GetString()));
		Assert.Equal(11, bare.RootElement.GetProperty("terminal").GetProperty("size").GetInt32());

		using var message = JsonDocument.Parse(FontSettings.BuildJson(store, "fonts"));
		Assert.Equal("fonts", message.RootElement.GetProperty("type").GetString());
	}

	[Fact]
	public void RegisteredInCoreCatalog_WithFontKeys() {
		using var store = CoreSettings.CreateStore(FilePath, enableWatcher: false);

		using var catalog = JsonDocument.Parse(store.BuildCatalogJson());
		var keys = catalog.RootElement.GetProperty("settings")
			.EnumerateArray().Select(s => s.GetProperty("key").GetString()).ToList();

		Assert.Contains(FontSettings.GlobalFamily, keys);
		Assert.Contains(FontSettings.EditorSize, keys);
		Assert.Contains(FontSettings.TerminalWeight, keys);
	}
}
