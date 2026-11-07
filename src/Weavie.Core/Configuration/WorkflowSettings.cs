namespace Weavie.Core.Configuration;

/// <summary>Registers the workflow settings. See docs/specs/workflows.md.</summary>
public static class WorkflowSettings {
	/// <summary>How many rejected completions of one stage pause the run.</summary>
	public const string MaxStageAttempts = "workflow.maxStageAttempts";

	/// <summary>Registers workflow settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new SettingDefinition {
			Key = MaxStageAttempts,
			Kind = SettingKind.Int,
			Description = "How many times a workflow stage's completion can be rejected (missing or mistyped outputs) before the run pauses for you.",
			Aliases = ["workflow attempts", "stage attempts", "workflow retries"],
			Apply = ApplyMode.Live,
			Default = 3L,
			Validate = value => value is long attempts && attempts >= 1
				? ValidationResult.Success
				: ValidationResult.Failure("must be an integer of at least 1"),
		});
	}
}
