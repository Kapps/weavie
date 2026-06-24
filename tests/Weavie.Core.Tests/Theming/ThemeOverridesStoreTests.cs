using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies the per-theme overrides store: ordered append, per-theme isolation, undo/clear, a polymorphic
/// (set + transform) persistence round-trip, and the malformed-file backup-and-reset contract.
/// </summary>
public sealed class ThemeOverridesStoreTests {
	private static string TempPath() => Path.Combine(Path.GetTempPath(), $"weavie-theme-overrides-{Guid.NewGuid():N}.json");

	[Fact]
	public void Append_ThenGet_ReturnsOpsInOrder() {
		var store = new ThemeOverridesStore(new InMemoryFileSystem(), TempPath());
		store.Append("weavie-dark", new ThemeOverrideSet { Key = "editor.background", Value = "#000000" });
		store.Append("weavie-dark", new ThemeOverrideTransform { Op = "darken", Amount = 0.2 });

		var ops = store.Get("weavie-dark");
		Assert.Equal(2, ops.Count);
		var set = Assert.IsType<ThemeOverrideSet>(ops[0]);
		Assert.Equal("editor.background", set.Key);
		Assert.Equal("#000000", set.Value);
		Assert.IsType<ThemeOverrideTransform>(ops[1]);
	}

	[Fact]
	public void Overrides_AreIsolatedPerTheme() {
		var store = new ThemeOverridesStore(new InMemoryFileSystem(), TempPath());
		store.Append("weavie-dark", new ThemeOverrideSet { Key = "editor.background", Value = "#000000" });

		Assert.Single(store.Get("weavie-dark"));
		Assert.Empty(store.Get("dracula-theme.theme-dracula/Dracula"));
	}

	[Fact]
	public void Persistence_RoundTrips_PolymorphicOps() {
		var fs = new InMemoryFileSystem();
		string path = TempPath();
		var store = new ThemeOverridesStore(fs, path);
		store.Append("dracula", new ThemeOverrideSet { Key = "editor.foreground", Value = "#ff00ff" });
		store.Append("dracula", new ThemeOverrideTransform { Op = "lighten", Amount = 0.1, Target = "all" });

		// A fresh store must read the ops back with their concrete kinds intact.
		var reloaded = new ThemeOverridesStore(fs, path);
		var ops = reloaded.Get("dracula");
		Assert.Equal(2, ops.Count);
		Assert.Equal("#ff00ff", Assert.IsType<ThemeOverrideSet>(ops[0]).Value);
		var transform = Assert.IsType<ThemeOverrideTransform>(ops[1]);
		Assert.Equal("lighten", transform.Op);
		Assert.Equal(0.1, transform.Amount);
		Assert.Equal("all", transform.Target);
	}

	[Fact]
	public void Persistence_RoundTrips_FontStyleSet_WithNoColor() {
		var fs = new InMemoryFileSystem();
		string path = TempPath();
		var store = new ThemeOverridesStore(fs, path);
		// Style-only override: fontStyle, no foreground value.
		store.Append("weavie-dark", new ThemeOverrideSet {
			Table = "semanticTokenColors",
			Key = "variable",
			FontStyle = "italic",
		});
		// One that sets both color + style.
		store.Append("weavie-dark", new ThemeOverrideSet {
			Table = "tokenColors",
			Key = "comment",
			Value = "#6a6a6a",
			FontStyle = "italic",
		});

		var reloaded = new ThemeOverridesStore(fs, path);
		var ops = reloaded.Get("weavie-dark");
		Assert.Equal(2, ops.Count);
		var styleOnly = Assert.IsType<ThemeOverrideSet>(ops[0]);
		Assert.Null(styleOnly.Value);
		Assert.Equal("italic", styleOnly.FontStyle);
		Assert.Equal("semanticTokenColors", styleOnly.Table);
		var both = Assert.IsType<ThemeOverrideSet>(ops[1]);
		Assert.Equal("#6a6a6a", both.Value);
		Assert.Equal("italic", both.FontStyle);
	}

	[Fact]
	public void UndoLast_RemovesLastOp_AndReportsWhetherAnythingWasThere() {
		var store = new ThemeOverridesStore(new InMemoryFileSystem(), TempPath());
		store.Append("t", new ThemeOverrideSet { Key = "a", Value = "#111111" });
		store.Append("t", new ThemeOverrideSet { Key = "b", Value = "#222222" });

		Assert.True(store.UndoLast("t"));
		Assert.Equal("a", Assert.IsType<ThemeOverrideSet>(Assert.Single(store.Get("t"))).Key);
		Assert.True(store.UndoLast("t"));
		Assert.Empty(store.Get("t"));
		Assert.False(store.UndoLast("t"));
	}

	[Fact]
	public void Clear_RemovesAllOpsForTheme() {
		var store = new ThemeOverridesStore(new InMemoryFileSystem(), TempPath());
		store.Append("t", new ThemeOverrideSet { Key = "a", Value = "#111111" });

		Assert.True(store.Clear("t"));
		Assert.Empty(store.Get("t"));
		Assert.False(store.Clear("t"));
	}

	[Fact]
	public void SetOps_ReplacesWholesale_AndEmptyClears() {
		var fs = new InMemoryFileSystem();
		string path = TempPath();
		var store = new ThemeOverridesStore(fs, path);
		store.Append("t", new ThemeOverrideSet { Key = "a", Value = "#111111" });

		// A non-empty list replaces the theme's ops wholesale and round-trips.
		store.SetOps("t", [new ThemeOverrideSet { Key = "b", Value = "#222222" }]);
		Assert.Equal("b", Assert.IsType<ThemeOverrideSet>(Assert.Single(store.Get("t"))).Key);
		Assert.Equal("b", Assert.IsType<ThemeOverrideSet>(Assert.Single(new ThemeOverridesStore(fs, path).Get("t"))).Key);

		// An empty list clears the theme entirely.
		store.SetOps("t", []);
		Assert.Empty(store.Get("t"));
	}

	[Fact]
	public void Changed_FiresWithThemeId() {
		var store = new ThemeOverridesStore(new InMemoryFileSystem(), TempPath());
		string? changed = null;
		store.Changed += id => changed = id;

		store.Append("weavie-dark", new ThemeOverrideSet { Key = "a", Value = "#111111" });
		Assert.Equal("weavie-dark", changed);
	}

	[Fact]
	public void MalformedFile_IsBackedUpAndReset() {
		string path = TempPath();
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(path, "this is not json");

		var store = new ThemeOverridesStore(fs, path);
		Assert.Empty(store.Get("anything"));
		Assert.True(fs.FileExists(path + ".bad"));
		Assert.Equal("this is not json", fs.ReadAllText(path + ".bad"));
	}
}
