using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>
/// Wires the Core handler for the blame toggle. It flips <see cref="EditorSettings.GitBlame"/> in the settings
/// store rather than holding its own on/off flag, so the toggle, the setting, and the MCP surface can never
/// disagree — and the choice survives a restart.
/// </summary>
public static class GitBlameCommands {
	/// <summary>Registers the blame-toggle handler onto <paramref name="dispatcher"/>.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		dispatcher.RegisterHandler(CoreCommands.ToggleBlame, (_, _) => Task.FromResult(Toggle(settings)));
	}

	// Off from any showing mode; back to the default from off. Toggling off and on therefore lands on
	// 'currentLine' even for someone who had chosen 'all' — a toggle only carries one bit, so the mode it
	// restores has to be a fixed one. Changing modes is the setting's job, not this command's.
	private static CommandResult Toggle(SettingsStore settings) {
		try {
			bool showing = !string.Equals(
				settings.RequireString(EditorSettings.GitBlame),
				EditorSettings.GitBlameOff,
				StringComparison.Ordinal);
			string next = showing ? EditorSettings.GitBlameOff : EditorSettings.GitBlameCurrentLine;
			using var value = JsonDocument.Parse(JsonSerializer.Serialize(next));
			return settings.Set(EditorSettings.GitBlame, value.RootElement).ShadowedByEnv is { Length: > 0 } variable
				? CommandResult.Success(
					$"Set 'editor.gitBlame' to '{next}', but {variable} overrides it; unset it to see the change.")
				: CommandResult.Success();
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}
