using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>
/// Wires the Core handlers that show or hide each kind of review item. Each flips its setting rather than holding its
/// own flag, so the filter menu, the palette, and an agent changing the setting never disagree.
/// </summary>
public static class ReviewItemCommands {
	/// <summary>Registers the review item toggle handlers onto <paramref name="dispatcher"/>.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		foreach (var (command, key) in new[] {
			(CoreCommands.PullRequestToggleComments, EditorSettings.ReviewComments),
			(CoreCommands.ReviewHintsToggle, EditorSettings.ReviewHints),
			(CoreCommands.ReviewToggleSettledItems, EditorSettings.ReviewStepSettled),
		}) {
			dispatcher.RegisterHandler(command, (_, _) => Task.FromResult(Toggle(settings, key)));
		}
	}

	private static CommandResult Toggle(SettingsStore settings, string key) {
		try {
			bool next = !settings.RequireBool(key);
			return settings.Set(key, JsonSerializer.SerializeToElement(next, CoreJson.Default.Boolean)).ShadowedByEnv is { Length: > 0 } variable
				? CommandResult.Success($"Set '{key}' to {(next ? "true" : "false")}, but {variable} overrides it; unset it to see the change.")
				: CommandResult.Success();
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}
