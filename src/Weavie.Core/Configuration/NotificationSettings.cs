using Weavie.Core.Json;

namespace Weavie.Core.Configuration;

/// <summary>
/// Session-attention notification settings: master switches for sounds and OS notifications, the volume,
/// the active sound pack, and per-event gates. All <see cref="ApplyMode.Live"/>: a change re-pushes the
/// resolved prefs to the web, which presents every attention event. See docs/specs/session-attention.md.
/// </summary>
public static class NotificationSettings {
	/// <summary>Master switch for attention sounds.</summary>
	public const string Sounds = "notifications.sounds";

	/// <summary>Master switch for OS notifications.</summary>
	public const string Os = "notifications.os";

	/// <summary>Attention sound volume, 0–100.</summary>
	public const string Volume = "notifications.volume";

	/// <summary>The sound pack attention sounds play from.</summary>
	public const string SoundPack = "notifications.soundPack";

	/// <summary>Per-event gate: a session finished its turn.</summary>
	public const string OnTurnComplete = "notifications.onTurnComplete";

	/// <summary>Per-event gate: a session is blocked on the user (permission prompt / waiting for input).</summary>
	public const string OnNeedsInput = "notifications.onNeedsInput";

	/// <summary>Per-event gate: a session's agent crashed.</summary>
	public const string OnFailed = "notifications.onFailed";

	/// <summary>Every notification setting key — the host subscribes to all of them to re-push on any change.</summary>
	public static readonly IReadOnlyList<string> Keys = [
		Sounds, Os, Volume, SoundPack, OnTurnComplete, OnNeedsInput, OnFailed,
	];

	/// <summary>The bundled CESP sound packs (web/public/sounds/&lt;pack&gt;/) selectable as <see cref="SoundPack"/>.</summary>
	public static readonly string[] Packs = ["weavie"];

	private const long DefaultVolume = 70;

	/// <summary>Registers the notification settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = Sounds,
			Kind = SettingKind.Bool,
			Description = "Play a sound when a session wants attention (turn complete, waiting on you, or "
				+ "crashed). Sounds play on your client, so a remote session's ping is heard locally. The "
				+ "session you're actively looking at never pings.",
			Aliases = ["notification sounds", "attention sounds", "sound alerts", "mute sounds", "play sounds"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = Os,
			Kind = SettingKind.Bool,
			Description = "Show an OS notification naming the session that wants attention when the Weavie "
				+ "window isn't focused; clicking it focuses that session. Notifications are silent — the "
				+ "sound comes from notifications.sounds.",
			Aliases = ["os notifications", "desktop notifications", "system notifications", "notify me"],
			Apply = ApplyMode.Live,
			Default = true,
		});

		registry.Register(new SettingDefinition {
			Key = Volume,
			Kind = SettingKind.Int,
			Description = "Attention sound volume, 0–100.",
			Aliases = ["notification volume", "sound volume", "alert volume"],
			Apply = ApplyMode.Live,
			Default = DefaultVolume,
			Validate = static value => value is long volume and >= 0 and <= 100
				? ValidationResult.Success
				: ValidationResult.Failure("notifications.volume must be between 0 and 100."),
		});

		registry.Register(new SettingDefinition {
			Key = SoundPack,
			Kind = SettingKind.String,
			Description = "The sound pack attention sounds play from (a bundled CESP pack).",
			Aliases = ["sound pack", "notification pack", "sound theme", "peon pack"],
			AllowedValues = Packs,
			Apply = ApplyMode.Live,
			Default = Packs[0],
		});

		RegisterEventGate(registry, OnTurnComplete, "a session finishes its turn",
			["notify on turn complete", "turn complete notification", "ping when done"]);
		RegisterEventGate(registry, OnNeedsInput, "a session is waiting on you (a permission prompt or input)",
			["notify on needs input", "permission notification", "ping when blocked"]);
		RegisterEventGate(registry, OnFailed, "a session's agent crashes",
			["notify on failure", "crash notification", "ping on error"]);
	}

	/// <summary>
	/// Serializes the resolved prefs as JSON. With <paramref name="messageType"/> set, a <c>"type"</c> field is
	/// written first (for a bridge push); when null, the bare object is produced (for the injected
	/// <c>window.__WEAVIE_NOTIFICATIONS__</c> global).
	/// </summary>
	public static string BuildJson(SettingsStore store, string? messageType) {
		ArgumentNullException.ThrowIfNull(store);
		return JsonWrite.Object(writer => {
			if (messageType is not null) {
				writer.WriteString("type", messageType);
			}

			writer.WriteBoolean("sounds", store.GetBool(Sounds, true));
			writer.WriteBoolean("os", store.GetBool(Os, true));
			writer.WriteNumber("volume", store.GetInt(Volume, DefaultVolume));
			writer.WriteString("soundPack", store.GetString(SoundPack) ?? Packs[0]);
			writer.WriteBoolean("onTurnComplete", store.GetBool(OnTurnComplete, true));
			writer.WriteBoolean("onNeedsInput", store.GetBool(OnNeedsInput, true));
			writer.WriteBoolean("onFailed", store.GetBool(OnFailed, true));
		});
	}

	private static void RegisterEventGate(
		SettingsRegistry registry, string key, string trigger, IReadOnlyList<string> aliases) {
		registry.Register(new SettingDefinition {
			Key = key,
			Kind = SettingKind.Bool,
			Description = $"Notify (sound + OS notification, per their switches) when {trigger}.",
			Aliases = aliases,
			Apply = ApplyMode.Live,
			Default = true,
		});
	}
}
