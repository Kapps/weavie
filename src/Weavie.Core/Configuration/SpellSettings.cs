using Weavie.Core.Json;
using Weavie.Core.Spelling;

namespace Weavie.Core.Configuration;

/// <summary>Live spell-check settings exposed through settings files and MCP.</summary>
public static class SpellSettings {
	/// <summary>Whether open editor documents receive spelling decorations.</summary>
	public const string Enabled = "spell.enabled";

	/// <summary>The Hunspell locale used to check open editor documents.</summary>
	public const string Locale = "spell.locale";

	/// <summary>Every spell-check setting key.</summary>
	public static IReadOnlyList<string> Keys { get; } = [Enabled, Locale];

	/// <summary>Registers the spell-check settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = Enabled,
			Kind = SettingKind.Bool,
			Description = "Underline likely misspellings throughout open editor documents. "
				+ "Turn this off to hide spelling decorations.",
			Aliases = ["spell check", "spelling", "typo underlines", "spell checker"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = Locale,
			Kind = SettingKind.String,
			Description = "English dictionary locale used by spell check. Set it in settings.toml or through MCP; "
				+ "a locale change immediately rechecks open editor documents.",
			Aliases = ["spell locale", "spelling locale", "dictionary locale", "english locale"],
			AllowedValues = SpellLocales.Supported,
			Apply = ApplyMode.Live,
			Default = SpellLocales.EnUs,
		});
	}

	/// <summary>Builds resolved spell settings for the bootstrap global or a live bridge push.</summary>
	public static string BuildJson(SettingsStore store, string? messageType) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.Object(writer => {
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			writer.WriteBoolean("enabled", store.RequireBool(Enabled));
			writer.WriteString("locale", store.RequireString(Locale));
		});
	}
}
