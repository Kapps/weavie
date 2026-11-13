using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>Writes one setting on a command's behalf, reporting a shadowing environment variable or a refused value.</summary>
public static class SettingCommand {
	/// <summary>Sets <paramref name="key"/> to <paramref name="value"/>, shown to the user as <paramref name="shown"/>.</summary>
	public static CommandResult Set(SettingsStore settings, string key, JsonElement value, string shown) {
		ArgumentNullException.ThrowIfNull(settings);
		try {
			return settings.Set(key, value).ShadowedByEnv is { Length: > 0 } variable
				? CommandResult.Success($"Set '{key}' to {shown}, but {variable} overrides it; unset it to see the change.")
				: CommandResult.Success();
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}
