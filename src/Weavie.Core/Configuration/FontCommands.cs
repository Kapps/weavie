using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>
/// Wires the Core handlers for the font-zoom commands (increase / decrease / reset font size). They adjust the
/// global <c>font.size</c> setting, which the web applies live to both the editor and terminal.
/// </summary>
public static class FontCommands {
	/// <summary>Registers the font-zoom command handlers onto <paramref name="dispatcher"/>.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		dispatcher.RegisterHandler(CoreCommands.IncreaseFontSize, (_, _) => Task.FromResult(Adjust(settings, +1)));
		dispatcher.RegisterHandler(CoreCommands.DecreaseFontSize, (_, _) => Task.FromResult(Adjust(settings, -1)));
		dispatcher.RegisterHandler(CoreCommands.ResetFontSize, (_, _) =>
			Task.FromResult(SetSize(settings, FontSettings.DefaultSize)));
	}

	// Steps the font size by one px, clamped. At a bound it reports the limit (so the key isn't a silent
	// no-op there); otherwise success is silent — the live resize on screen is the feedback.
	private static CommandResult Adjust(SettingsStore settings, int delta) {
		long current = settings.GetInt(FontSettings.GlobalSize, FontSettings.DefaultSize);
		long next = FontSettings.ClampSize(current + delta);
		if (next == current) {
			return CommandResult.Success($"Font size is already at its {(delta > 0 ? "maximum" : "minimum")} ({current}px).");
		}

		return SetSize(settings, next);
	}

	private static CommandResult SetSize(SettingsStore settings, long size) {
		try {
			using var doc = JsonDocument.Parse(JsonSerializer.Serialize(size));
			settings.Set(FontSettings.GlobalSize, doc.RootElement);
			return CommandResult.Success();
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}
}
