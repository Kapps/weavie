namespace Weavie.Core.Configuration;

/// <summary>
/// Registers theme-related settings. The ACTIVE theme is a normal persisted setting — so it survives via
/// <c>settings.toml</c> and is editable conversationally over MCP like any other setting. The per-color
/// OVERRIDES are deliberately NOT a setting: they live in their own <c>~/.weavie/theme-overrides.json</c>
/// document (see <see cref="Weavie.Core.Theming.ThemeOverridesStore"/>), like layout.
/// </summary>
public static class ThemeSettings {
	/// <summary>The default theme id — the built-in "Weavie Dark" (must match the web's <c>WEAVIE_DARK_ID</c>).</summary>
	public const string DefaultThemeId = "weavie-dark";

	/// <summary>Registers the theme settings (<c>theme.active</c>) into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = "theme.active",
			Kind = SettingKind.String,
			Description = "The active color theme: a built-in id (e.g. 'weavie-dark') or an installed theme id "
				+ "(e.g. 'dracula-theme.theme-dracula/Dracula'). Drives the editor, the terminal, and Weavie's "
				+ "own UI; per-theme color tweaks are layered on top as overrides. Takes effect immediately.",
			Aliases = ["theme", "color theme", "colour theme", "active theme", "color scheme", "colour scheme"],
			Apply = ApplyMode.Live,
			Default = DefaultThemeId,
		});
	}
}
