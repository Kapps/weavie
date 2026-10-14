namespace Weavie.Core.Configuration;

/// <summary>
/// Registers theme settings: <c>theme.mode</c> (<c>system</c>/<c>light</c>/<c>dark</c>), the per-polarity
/// <c>theme.light</c> / <c>theme.dark</c> theme ids, and the optional per-polarity code-editor themes
/// <c>theme.editorLight</c> / <c>theme.editorDark</c> (empty follows the app theme). Per-color OVERRIDES are deliberately not settings — they
/// live in <c>~/.weavie/theme-overrides.json</c> (see <see cref="Weavie.Core.Theming.ThemeOverridesStore"/>).
/// <para>
/// The web layer is authoritative for rendering: it resolves <c>system</c> against the live
/// <c>prefers-color-scheme</c>. Core can't see the OS setting, so for override targeting it resolves
/// <c>system</c> to the dark slot — Weavie's default polarity (see <see cref="ResolveActiveThemeId"/>).
/// </para>
/// </summary>
public static class ThemeSettings {
	/// <summary>The default dark theme id — the built-in "Weavie Dark" (must match the web's <c>WEAVIE_DARK_ID</c>).</summary>
	public const string DefaultDarkThemeId = "weavie-dark";

	/// <summary>The default light theme id — the built-in "Weavie Light" (must match the web's <c>WEAVIE_LIGHT_ID</c>).</summary>
	public const string DefaultLightThemeId = "weavie-light";

	/// <summary>The default appearance mode — follow the OS light/dark setting.</summary>
	public const string DefaultMode = "system";

	/// <summary>Default theme id (the dark slot), for callers that need a built-in fallback.</summary>
	public const string DefaultThemeId = DefaultDarkThemeId;

	/// <summary>Setting key: the appearance mode (<c>system</c>/<c>light</c>/<c>dark</c>).</summary>
	public const string ModeKey = "theme.mode";

	/// <summary>Setting key: the theme used in light mode.</summary>
	public const string LightKey = "theme.light";

	/// <summary>Setting key: the theme used in dark mode.</summary>
	public const string DarkKey = "theme.dark";

	/// <summary>Setting key: the code-editor theme used in light mode; empty follows <see cref="LightKey"/>.</summary>
	public const string EditorLightKey = "theme.editorLight";

	/// <summary>Setting key: the code-editor theme used in dark mode; empty follows <see cref="DarkKey"/>.</summary>
	public const string EditorDarkKey = "theme.editorDark";

	/// <summary>The theme setting keys whose change re-pushes the resolved theme pair to the web.</summary>
	public static IReadOnlySet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal) {
		ModeKey, LightKey, DarkKey, EditorLightKey, EditorDarkKey,
	};

	/// <summary>Registers the theme settings.</summary>
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

		RegisterEditorTheme(registry, EditorLightKey, "light", ["editor light theme", "code light theme", "syntax light theme"]);
		RegisterEditorTheme(registry, EditorDarkKey, "dark", ["editor dark theme", "code dark theme", "syntax dark theme"]);
	}

	private static void RegisterEditorTheme(SettingsRegistry registry, string key, string polarity, string[] aliases) =>
		registry.Register(new SettingDefinition {
			Key = key,
			Kind = SettingKind.String,
			Description = $"The color theme for the code editor and diff views in {polarity} mode, independent of the "
				+ $"app theme (theme.{polarity}) used by chat, panels, and terminals. Empty (the default) makes the editor "
				+ "follow the app theme. Accepts the same ids as theme.light/theme.dark; set both editor keys to one id "
				+ "to keep the editor fixed across light/dark mode. Takes effect immediately.",
			Aliases = ["editor theme", "code theme", "code editor theme", "syntax theme", .. aliases],
			Apply = ApplyMode.Live,
			Default = "",
		});

	/// <summary>The current appearance mode (<c>system</c>/<c>light</c>/<c>dark</c>), defaulting to <c>system</c>.</summary>
	public static string Mode(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.RequireString(ModeKey);
	}

	/// <summary>The selected light-mode theme id.</summary>
	public static string LightThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.RequireString(LightKey);
	}

	/// <summary>The selected dark-mode theme id.</summary>
	public static string DarkThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.RequireString(DarkKey);
	}

	/// <summary>The code-editor theme id for light mode, or empty to follow the app theme.</summary>
	public static string EditorLightThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.RequireString(EditorLightKey);
	}

	/// <summary>The code-editor theme id for dark mode, or empty to follow the app theme.</summary>
	public static string EditorDarkThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return settings.RequireString(EditorDarkKey);
	}

	/// <summary>
	/// The concrete theme id Core treats as "active" for per-theme overrides. Core can't see
	/// <c>prefers-color-scheme</c>, so it resolves <c>system</c> to the dark slot (Weavie's default polarity).
	/// </summary>
	public static string ResolveActiveThemeId(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		return Mode(settings) == "light" ? LightThemeId(settings) : DarkThemeId(settings);
	}

	/// <summary>True if <paramref name="themeId"/> is one of the selected app or editor theme ids — an
	/// override on it could be live, so a change to it warrants re-pushing the theme payload.</summary>
	public static bool IsSelectedThemeId(SettingsStore settings, string themeId) {
		ArgumentNullException.ThrowIfNull(settings);
		return themeId == LightThemeId(settings) || themeId == DarkThemeId(settings)
			|| themeId == EditorLightThemeId(settings) || themeId == EditorDarkThemeId(settings);
	}
}
