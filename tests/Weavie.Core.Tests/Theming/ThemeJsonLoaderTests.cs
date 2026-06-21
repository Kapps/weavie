using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies theme include-merge: a theme that <c>include</c>s another inherits its colors/tokens,
/// overriding colors per key and appending token rules; no-include themes pass through unchanged;
/// and an include cycle is detected rather than recursing forever.
/// </summary>
public sealed class ThemeJsonLoaderTests {
	[Fact]
	public void LoadMerged_ResolvesIncludeChain() {
		string dir = Path.Combine(Path.GetTempPath(), $"weavie-theme-{Guid.NewGuid():N}");
		string basePath = Path.Combine(dir, "base.json");
		string childPath = Path.Combine(dir, "child.json");
		var fs = new InMemoryFileSystem(new Dictionary<string, string> {
			[basePath] = """
			{ "name": "Base", "type": "dark",
			  "colors": { "editor.background": "#111111", "editor.foreground": "#eeeeee" },
			  "tokenColors": [ { "scope": "comment", "settings": { "foreground": "#aaaaaa" } } ] }
			""",
			[childPath] = """
			{ "include": "./base.json", "name": "Child",
			  "colors": { "editor.foreground": "#ffffff", "focusBorder": "#00ff00" },
			  "tokenColors": [ { "scope": "keyword", "settings": { "foreground": "#ff00ff" } } ] }
			""",
		});

		var merged = new ThemeJsonLoader(fs).LoadMerged(childPath);

		Assert.False(merged.ContainsKey("include"));
		Assert.Equal("Child", (string?)merged["name"]);
		var colors = merged["colors"]!.AsObject();
		Assert.Equal("#111111", (string?)colors["editor.background"]); // inherited from base
		Assert.Equal("#ffffff", (string?)colors["editor.foreground"]); // child overrides
		Assert.Equal("#00ff00", (string?)colors["focusBorder"]); // child adds
		var tokens = merged["tokenColors"]!.AsArray();
		Assert.Equal(2, tokens.Count); // base + child appended
		Assert.Equal("comment", (string?)tokens[0]!["scope"]); // base first
		Assert.Equal("keyword", (string?)tokens[1]!["scope"]); // child last
	}

	[Fact]
	public void LoadMerged_NoInclude_ReturnsAsIs() {
		string path = Path.Combine(Path.GetTempPath(), $"weavie-theme-{Guid.NewGuid():N}.json");
		var fs = new InMemoryFileSystem(new Dictionary<string, string> {
			[path] = """{ "name": "Solo", "type": "dark", "colors": { "editor.background": "#000000" } }""",
		});

		var merged = new ThemeJsonLoader(fs).LoadMerged(path);

		Assert.Equal("Solo", (string?)merged["name"]);
		Assert.Equal("#000000", (string?)merged["colors"]!.AsObject()["editor.background"]);
	}

	[Fact]
	public void LoadMerged_CyclicInclude_Throws() {
		string dir = Path.Combine(Path.GetTempPath(), $"weavie-theme-{Guid.NewGuid():N}");
		string a = Path.Combine(dir, "a.json");
		string b = Path.Combine(dir, "b.json");
		var fs = new InMemoryFileSystem(new Dictionary<string, string> {
			[a] = """{ "include": "./b.json", "colors": {} }""",
			[b] = """{ "include": "./a.json", "colors": {} }""",
		});

		Assert.Throws<InvalidOperationException>(() => new ThemeJsonLoader(fs).LoadMerged(a));
	}
}
