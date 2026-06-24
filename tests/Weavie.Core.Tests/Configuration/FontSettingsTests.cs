using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Tests <see cref="FontSettings"/>: global default, per-surface override precedence, the inherit
/// sentinels (empty family, <c>0</c> size, <c>"inherit"</c> weight), validation, and the host JSON.
/// Uses an isolated font-only registry over a temp file.
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

	// The registered global default size, read from resolution instead of hardcoded — a change to the default
	// const ripples here automatically, keeping these tests about inheritance rather than a magic number.
	private static long GlobalDefaultSize(SettingsStore store) => (long)store.Resolve(FontSettings.GlobalSize).Value!;

	private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

	[Fact]
	public void Defaults_SizeAndWeightInheritGlobal_EachSurfaceFamilyDefaultsToItsBundledMono() {
		using var store = NewStore();

		var editor = FontSettings.ResolveEditor(store);
		var terminal = FontSettings.ResolveTerminal(store);

		// Size and weight inherit the global default on both surfaces.
		long globalDefault = GlobalDefaultSize(store);
		Assert.Equal(globalDefault, editor.Size);
		Assert.Equal(globalDefault, terminal.Size);
		Assert.Equal("normal", editor.Weight);
		Assert.Equal("normal", terminal.Weight);

		// Each surface's family has its own default leading with a bundled font (editor → Go Mono,
		// terminal → JetBrains Mono); both end in generic monospace, and they don't match each other.
		Assert.Contains("Go Mono", editor.Family, StringComparison.Ordinal);
		Assert.Contains("monospace", editor.Family, StringComparison.Ordinal);
		Assert.Contains("JetBrains Mono", terminal.Family, StringComparison.Ordinal);
		Assert.Contains("monospace", terminal.Family, StringComparison.Ordinal);
		Assert.NotEqual(editor.Family, terminal.Family);
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
		// Terminal untouched — still resolves to its own family default.
		Assert.Equal(GlobalDefaultSize(store), terminal.Size);
		Assert.Contains("monospace", terminal.Family, StringComparison.Ordinal);
	}

	[Fact]
	public void ConcreteWeightOverride_WinsOverGlobal_ForItsSurfaceOnly() {
		using var store = NewStore();
		store.Set(FontSettings.GlobalWeight, Json("\"300\""));
		store.Set(FontSettings.EditorWeight, Json("\"bold\"")); // a concrete override, not the inherit sentinel

		Assert.Equal("bold", FontSettings.ResolveEditor(store).Weight);   // editor override wins
		Assert.Equal("300", FontSettings.ResolveTerminal(store).Weight);  // terminal still inherits the global
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
		// Editor inherit sentinels: empty family, 0 size, "inherit" weight.
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

		// Override size 0 (inherit) is allowed; a non-zero out-of-range one is not.
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
		Assert.Equal(GlobalDefaultSize(store), editor.GetProperty("size").GetInt64());
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
