using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Core.Configuration;

/// <summary>
/// Wires the Core handlers for the font-zoom commands (increase / decrease / reset font size). They step every
/// size in effect — the global <c>font.size</c> plus any set per-surface override — so the rendered fonts always
/// follow the command, and they report an environment-variable shadow instead of silently doing nothing.
/// </summary>
public static class FontCommands {
	// The per-surface size overrides; when set (> 0) they are the rendered sizes, so zoom must step them too.
	private static readonly string[] OverrideKeys = [FontSettings.EditorSize, FontSettings.TerminalSize];

	/// <summary>Registers the font-zoom command handlers onto <paramref name="dispatcher"/>.</summary>
	public static void RegisterHandlers(CommandDispatcher dispatcher, SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(settings);
		dispatcher.RegisterHandler(CoreCommands.IncreaseFontSize, (_, _) => Task.FromResult(Adjust(settings, +1)));
		dispatcher.RegisterHandler(CoreCommands.DecreaseFontSize, (_, _) => Task.FromResult(Adjust(settings, -1)));
		dispatcher.RegisterHandler(CoreCommands.ResetFontSize, (_, _) => Task.FromResult(Reset(settings)));
	}

	// Steps each size in effect by one px, clamped. Success is silent — the live resize is the feedback — but a
	// no-op is not: an env shadow or a bound is reported, so the key never silently does nothing.
	private static CommandResult Adjust(SettingsStore settings, int delta) {
		try {
			var shadows = new List<string>();
			var before = RenderedSizes(settings);
			Step(settings, FontSettings.GlobalSize, settings.GetInt(FontSettings.GlobalSize, FontSettings.DefaultSize), delta, shadows);
			foreach (string key in OverrideKeys) {
				long current = settings.GetInt(key, 0);
				if (current > 0) {
					Step(settings, key, current, delta, shadows);
				}
			}

			if (RenderedSizes(settings) != before) {
				return CommandResult.Success(); // something visibly resized — that is the feedback
			}

			return shadows.Count > 0
				? CommandResult.Success(ShadowMessage(shadows))
				: CommandResult.Success($"Font size is already at its {(delta > 0 ? "maximum" : "minimum")}.");
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	// Restores the default: global back to DefaultSize, per-surface overrides cleared (back to inherit).
	private static CommandResult Reset(SettingsStore settings) {
		try {
			var shadows = new List<string>();
			using var doc = JsonDocument.Parse(JsonSerializer.Serialize(FontSettings.DefaultSize));
			AddShadow(shadows, settings.Set(FontSettings.GlobalSize, doc.RootElement).ShadowedByEnv);
			foreach (string key in OverrideKeys) {
				AddShadow(shadows, settings.Clear(key).ShadowedByEnv);
			}

			return shadows.Count > 0
				? CommandResult.Success(ShadowMessage(shadows))
				: CommandResult.Success();
		} catch (Exception ex) when (
			ex is UnknownSettingException or SettingValidationException or SettingsFileMalformedException) {
			return CommandResult.Failure(ex.Message);
		}
	}

	private static string ShadowMessage(List<string> shadows) =>
		$"Font size is overridden by {string.Join(" and ", shadows)}; unset to change the size.";

	private static void Step(SettingsStore settings, string key, long current, int delta, List<string> shadows) {
		long next = FontSettings.ClampSize(current + delta);
		if (next == current) {
			return;
		}

		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(next));
		AddShadow(shadows, settings.Set(key, doc.RootElement).ShadowedByEnv);
	}

	private static void AddShadow(List<string> shadows, string? envVar) {
		if (envVar is not null && !shadows.Contains(envVar)) {
			shadows.Add(envVar);
		}
	}

	private static (long Editor, long Terminal) RenderedSizes(SettingsStore settings) =>
		(FontSettings.ResolveEditor(settings).Size, FontSettings.ResolveTerminal(settings).Size);
}
