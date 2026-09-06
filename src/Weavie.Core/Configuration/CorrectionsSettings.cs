namespace Weavie.Core.Configuration;

/// <summary>Registers the learn-from-corrections settings.</summary>
public static class CorrectionsSettings {
	/// <summary>Whether correction capture, learning, and its suggestion are enabled.</summary>
	public const string Enabled = "corrections.enabled";

	/// <summary>How many recorded corrections it takes before the "teach Claude" card appears.</summary>
	public const string LearnThreshold = "corrections.learnThreshold";

	/// <summary>Registers correction learning settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new SettingDefinition {
			Key = Enabled,
			Kind = SettingKind.Bool,
			Description = "Enable experimental correction capture and Learn From My Corrections. Disabled by default pending a full rework.",
			Aliases = ["learn from my corrections", "correction learning"],
			Apply = ApplyMode.Live,
			Default = false,
		});

		registry.Register(new SettingDefinition {
			Key = LearnThreshold,
			Kind = SettingKind.Int,
			Description = "How many post-turn corrections (reverted hunks / hand-edits to the agent's output) "
				+ "accumulate before the 'teach Claude from your corrections' card appears. The Learn From My "
				+ "Corrections command works at any count; this only gates the nudge.",
			Aliases = ["learn threshold", "corrections threshold", "when to suggest learning", "correction nudge count"],
			Apply = ApplyMode.Live,
			Default = 10L,
			Validate = value => value is long threshold && threshold >= 1
				? ValidationResult.Success
				: ValidationResult.Failure("must be an integer of at least 1"),
		});
	}
}
