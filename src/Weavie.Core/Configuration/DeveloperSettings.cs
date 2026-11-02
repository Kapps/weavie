namespace Weavie.Core.Configuration;

/// <summary>Registers developer diagnostics settings.</summary>
public static class DeveloperSettings {
	/// <summary>Whether the desktop app's web view accepts a web inspector (Safari Develop menu / Edge DevTools).</summary>
	public const string WebInspector = "developer.webInspector";

	/// <summary>Registers developer settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new SettingDefinition {
			Key = WebInspector,
			Kind = SettingKind.Bool,
			Description = "Allow attaching a web inspector to the desktop app's page (Safari's Develop menu on macOS, "
				+ "DevTools on Windows), e.g. to take a memory snapshot. Applies after restarting Weavie.",
			Aliases = ["dev tools", "devtools", "web inspector", "inspect element", "debug the page"],
			Apply = ApplyMode.RestartRequired,
			Default = false,
		});
	}
}
