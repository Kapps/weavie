using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;

namespace Weavie.Core.Inference;

/// <summary>Declares and handles the user opt-in for automatic ad-hoc inference.</summary>
public static class InferenceCommands {
	/// <summary>Registers the automatic-inference command definition.</summary>
	public static void Register(CommandRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new CommandDefinition {
			Id = CoreCommands.EnableAutomaticInference,
			Title = "Enable Automatic Inference",
			RunsIn = CommandLocation.Core,
			Category = "AI",
			Description = "Allow Weavie to make isolated automatic model calls for small product suggestions. "
				+ "Calls use the selected Claude or Codex provider and may spend tokens.",
			Aliases = ["automatic inference", "automatic AI suggestions", "AI branch names", "allow inference"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+alt+i" }],
		});
	}

	/// <summary>Registers the automatic-inference command handler.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		dispatcher.RegisterHandler(
			CoreCommands.EnableAutomaticInference,
			(_, _) => Task.FromResult(EnableAutomatic(settings)));
	}

	private static CommandResult EnableAutomatic(SettingsStore settings) {
		bool alreadyEnabled = IsEnabled(settings);
		try {
			var shadows = new List<string>();
			SetTrue(settings, InferenceSettings.AllowAutomatic, shadows);
			SetTrue(settings, InferenceSettings.Enabled, shadows);
			if (IsEnabled(settings)) {
				return CommandResult.Success(
					alreadyEnabled ? "Automatic inference is already enabled." : "Automatic inference enabled.");
			}

			return shadows.Count > 0
				? CommandResult.Failure(
					$"Automatic inference is overridden by {string.Join(" and ", shadows)}; unset it before enabling automatic inference.")
				: CommandResult.Failure("Automatic inference could not be enabled.");
		} catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or UnknownSettingException
				or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	private static bool IsEnabled(SettingsStore settings) =>
		settings.RequireBool(InferenceSettings.Enabled)
		&& settings.RequireBool(InferenceSettings.AllowAutomatic);

	private static void SetTrue(SettingsStore settings, string key, List<string> shadows) {
		string? shadow = settings.Set(key, JsonSerializer.SerializeToElement(true)).ShadowedByEnv;
		if (shadow is not null && !shadows.Contains(shadow)) {
			shadows.Add(shadow);
		}
	}
}
