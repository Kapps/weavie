namespace Weavie.Core.Configuration;

/// <summary>Settings for supervising inbound host and session message operations.</summary>
public static class MessageSettings {
	/// <summary>Maximum lifetime of one inbound operation, including queue and completion work.</summary>
	public const string OperationDeadlineSeconds = "messaging.operationDeadlineSeconds";

	/// <summary>The default operation deadline in seconds.</summary>
	public const long DefaultOperationDeadlineSeconds = 60;

	/// <summary>Registers message-operation settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new SettingDefinition {
			Key = OperationDeadlineSeconds,
			Kind = SettingKind.Int,
			Description = "Maximum seconds an inbound host or session message may spend queued, running its "
				+ "handler, and finishing response-owned work. A slow operation shows a busy toast; reaching this "
				+ "deadline faults its endpoint and makes a managed remote worker restart. Applies after restarting Weavie.",
			Aliases = ["message timeout", "handler timeout", "backend operation timeout", "message deadline"],
			Apply = ApplyMode.RestartRequired,
			Default = DefaultOperationDeadlineSeconds,
			Validate = static value => value is long seconds and >= 3 and <= 3600
				? ValidationResult.Success
				: ValidationResult.Failure("messaging.operationDeadlineSeconds must be between 3 and 3600 seconds."),
		});
	}
}
