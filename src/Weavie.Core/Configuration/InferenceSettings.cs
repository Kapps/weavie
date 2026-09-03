namespace Weavie.Core.Configuration;

/// <summary>Settings that gate and configure stateless ad-hoc model queries.</summary>
public static class InferenceSettings {
	/// <summary>Whether any ad-hoc inference operation may call its provider.</summary>
	public const string Enabled = "inference.enabled";

	/// <summary>Whether application-initiated inference may spend tokens without a direct user action.</summary>
	public const string AllowAutomatic = "inference.allowAutomatic";

	/// <summary>The agent provider used for every ad-hoc inference query.</summary>
	public const string DefaultProvider = "inference.defaultProvider";

	/// <summary>The provider-native model id, or empty to retain the provider/category default.</summary>
	public const string Model = "inference.model";

	/// <summary>The provider-native effort id, or empty to retain the provider/category default.</summary>
	public const string Effort = "inference.effort";

	/// <summary>Whether inference inherits, enables, or disables the provider's Fast Mode setting.</summary>
	public const string FastMode = "inference.fastMode";

	/// <summary>Registers the complete ad-hoc inference policy and provider profile.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new SettingDefinition {
			Key = Enabled,
			Kind = SettingKind.Bool,
			Description = "Allow Weavie features to make isolated model queries through the configured inference "
				+ "provider. Calls never enter the interactive session transcript. Off by default. Takes effect on "
				+ "the next query.",
			Aliases = ["ad hoc inference", "utility inference", "model queries", "ai suggestions"],
			Apply = ApplyMode.Live,
			Default = false,
		});
		registry.Register(new SettingDefinition {
			Key = AllowAutomatic,
			Kind = SettingKind.Bool,
			Description = "Allow Weavie to spend inference tokens without a directly-triggering user action, such as "
				+ "branch-name preview or continuous review after an edit. Explicit actions such as reviewing a plan or "
				+ "diagnosing a test failure do not require this. Off by default. Takes effect on the next query.",
			Aliases = ["automatic inference", "background ai suggestions", "continuous ai review"],
			Apply = ApplyMode.Live,
			Default = false,
		});
		registry.Register(new SettingDefinition {
			Key = DefaultProvider,
			Kind = SettingKind.String,
			Description = "Agent provider used for every ad-hoc inference query. Use 'claude' for Claude Code or an "
				+ "installed ACP provider id. Takes effect on the next query.",
			Aliases = ["inference provider", "default inference provider", "ai suggestion provider"],
			Apply = ApplyMode.Live,
			Default = "claude",
			Validate = static value => value is string provider && !string.IsNullOrWhiteSpace(provider)
				? ValidationResult.Success
				: ValidationResult.Failure("inference.defaultProvider must name an agent provider."),
		});
		registry.Register(new SettingDefinition {
			Key = Model,
			Kind = SettingKind.String,
			Description = "Provider-native model id for ad-hoc inference, such as 'opus'. Empty uses the provider's "
				+ "category default. Takes effect on the next query.",
			Aliases = ["inference model", "suggestion model", "ad hoc model"],
			Apply = ApplyMode.Live,
			Default = "",
		});
		registry.Register(new SettingDefinition {
			Key = Effort,
			Kind = SettingKind.String,
			Description = "Provider-native reasoning effort id for ad-hoc inference, such as 'low'. Empty uses the "
				+ "provider's category default. Takes effect on the next query.",
			Aliases = ["inference effort", "reasoning effort for inference", "suggestion effort"],
			Apply = ApplyMode.Live,
			Default = "",
		});
		registry.Register(new SettingDefinition {
			Key = FastMode,
			Kind = SettingKind.String,
			Description = "Fast Mode for ad-hoc inference: 'on', 'off', or 'inherit' to keep the provider default. "
				+ "Takes effect on the next query.",
			Aliases = ["inference fast mode", "fast inference", "priority inference"],
			AllowedValues = ["inherit", "on", "off"],
			Apply = ApplyMode.Live,
			Default = "inherit",
		});
	}
}
