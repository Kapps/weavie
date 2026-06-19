namespace Weavie.Core.Configuration;

/// <summary>
/// Registers theme-related settings. Appearance is split into a <b>mode</b> and a theme <b>per polarity</b>,
/// so light/dark is decoupled from the chosen theme: <c>theme.mode</c> is <c>system</c> (follow the OS,
/// default), <c>light</c>, or <c>dark</c>; <c>theme.light</c> / <c>theme.dark</c> name the theme used for each
/// polarity. All three are normal persisted settings — they survive via <c>settings.toml</c> and are editable
/// conversationally over MCP like any other setting. The per-color OVERRIDES are deliberately NOT settings:
/// they live in their own <c>~/.weavie/theme-overrides.json</c> document (see
/// <see cref="Weavie.Core.Theming.ThemeOverridesStore"/>), keyed per concrete theme id.
/// <para>
/// The WEB layer is authoritative for <em>rendering</em>: it resolves <c>system</c> against the live
/// <c>prefers-color-scheme</c> and renders the matching theme (the host injects/pushes BOTH the light and
/// dark themes as a pair so the switch is instant + flash-free). Core can't see the OS setting, so for
/// per-theme override targeting it resolves <c>system</c> to the dark slot — Weavie's default polarity (see
/// <see cref="ResolveActiveThemeId"/>).
/// </para>
/// </summary>
public static class ThemeSettings {
	/// <summary>The default dark theme id — the built-in "Weavie Dark" (must match the web's <c>WEAVIE_DARK_ID</c>).</summary>
	public const string DefaultDarkThemeId = "weavie-dark";

	/// <summary>The default light theme id — the built-in "Weavie Light" (must match the web's <c>WEAVIE_LIGHT_ID</c>).</summary>
	public const string DefaultLightThemeId = "weavie-light";

	/// <summary>The default appearance mode — follow the OS light/dark setting.</summary>
	public const string DefaultMode = "system";

	/// <summary>Back-compat default theme id (the dark slot), for callers that just need a built-in fallback.</summary>
	public const string DefaultThemeId = DefaultDarkThemeId;

	/// <summary>Setting key: the appearance mode (<c>system</c>/<c>light</c>/<c>dark</c>).</summary>
	public const string ModeKey = "theme.mode";

	/// <summary>Setting key: the theme used in light mode.</summary>
	public const string LightKey = "theme.light";

	/// <summary>Setting key: the theme used in dark mode.</summary>
	public const string DarkKey = "theme.dark";

	/// <summary>The theme setting keys whose change re-pushes the resolved theme pair to the web.</summary>
	public static IReadOnlySet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal) {
		ModeKey, LightKey, DarkKey,
	};

	/// <summary>Registers the theme settings (<c>theme.mode</c>/<c>theme.light</c>/<c>theme.dark</c>).</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = ModeKey,
			Kind = SettingKind.String,
			AllowedValues = ["system", "light", "dark"],
			Description = "Appearance mode: 'system' follows the OS light/dark setting (the default), 'light' always "
				+ "uses your light theme, 'dark' always uses your dark theme. The actual theme for each polarity is "
				+ "theme.light / theme.dark. Takes effect immediately.",
			Aliases = ["theme mode", "appearance", "appearance mode", "color mode", "colour mode", "dark mode", "light mode", "light or dark"],
			Apply = ApplyMode.Live,
			Default = DefaultMode,
		});

		registry.Register(new SettingDefinition {
			Key = LightKey,
			Kind = SettingKind.String,
			Description = "The color theme used in light mode (and in system mode when the OS is light): a built-in "
				+ "id (e.g. 'weavie-light') or an installed theme id (e.g. 'dracula-theme.theme-dracula/Dracula'). "
				+ "Takes effect immediately whenever light is the active polarity.",
			Aliases = ["light theme", "light mode theme", "light color theme", "light colour theme"],
			Apply = ApplyMode.Live,
			Default = DefaultLightThemeId,
		});

		registry.Register(new SettingDefinition {
			Key = DarkKey,
			Kind = SettingKind.String,
			Description = "The color theme used in dark mode (and in system mode when the OS is dark): a built-in "
				+ "id (e.g. 'weavie-dark') or an installed theme id (e.g. 'dracula-theme.theme-dracula/Dracula'). "
				+ "Takes effect immediately whenever dark is the active polarity.",
			Aliases = ["dark theme", "dark mode theme", "dark color theme", "dark colour theme"],
			Apply = ApplyMode.Live,
			Default = DefaultDarkThemeId,
		});
	}

	/// <summary>The current appearance mode (<c>system</c>/<c>light</c>/<c>dark</c>), defaulting to <c>system</c>.</summary>
	public static string Mode(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.GetString(ModeKey) ?? DefaultMode;
	}

	/// <summary>The selected light-mode theme id.</summary>
	public static string LightThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.GetString(LightKey) ?? DefaultLightThemeId;
	}

	/// <summary>The selected dark-mode theme id.</summary>
	public static string DarkThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.GetString(DarkKey) ?? DefaultDarkThemeId;
	}

	/// <summary>
	/// The concrete theme id Core treats as "active" for per-theme overrides (describe / set / undo / reset).
	/// The web is authoritative for what is RENDERED — it resolves <c>system</c> against the live OS setting —
	/// but Core can't see <c>prefers-color-scheme</c>, so it resolves <c>system</c> to the dark slot (Weavie's
	/// default polarity). After a theme is selected the mode is concrete (light/dark), so this is exact.
	/// </summary>
	public static string ResolveActiveThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return Mode(settings) == "light" ? LightThemeId(settings) : DarkThemeId(settings);
	}

	/// <summary>True if <paramref name="themeId"/> is one of the two selected (light/dark) theme ids — i.e. an
	/// override on it could be live, so a change to it warrants re-pushing the theme pair.</summary>
	public static bool IsSelectedThemeId(SettingsStore settings, string themeId) {
		ArgumentNullException.ThrowIfNull(settings);
		return themeId == LightThemeId(settings) || themeId == DarkThemeId(settings);
	}
}
