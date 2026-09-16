using System.Text.Json;

namespace Weavie.Core.Theming;

/// <summary>A selectable theme and its extension identity; built-ins have no extension coordinates.</summary>
public sealed record ThemeChoice(string Id, string Label, string Type, string? Namespace, string? Name, string? Version);

/// <summary>A theme choice and the self-contained slot used for live preview.</summary>
public sealed record ThemePreview(ThemeChoice Choice, JsonElement Slot);

/// <summary>The built-in and installed color theme catalog.</summary>
public static class ThemeCatalog {
	/// <summary>Lists selectable themes without loading their color data.</summary>
	public static IReadOnlyList<ThemeChoice> List() => BuiltInThemes.All
		.Select(t => new ThemeChoice(t.Id, t.Label, t.Type, null, null, null))
		.Concat(OpenVsxThemeInstaller.ListInstalled().Select(Describe)).ToArray();

	internal static ThemeChoice Describe(InstalledTheme theme) => new(
		theme.Id, theme.Label, theme.UiTheme is "vs" or "hc-light" ? "light" : "dark",
		theme.Namespace, theme.Name, theme.Version);
}
