namespace Weavie.Core.Configuration;

/// <summary>Registers the workflow settings. See docs/specs/workflows.md.</summary>
public static class WorkflowSettings {
	/// <summary>How many rejected completions of one stage pause the run.</summary>
	public const string MaxStageAttempts = "workflow.maxStageAttempts";

	/// <summary>Whether the agent opens what a workflow's question to the user is about.</summary>
	public const string OpenReviewItem = "workflow.openReviewItem";

	/// <summary>The minimum confidence required for a model decision to route automatically.</summary>
	public const string DecisionConfidence = "workflow.decisionConfidence";

	/// <summary>The workflow the new-session composer preselects.</summary>
	public const string Default = "workflow.default";

	/// <summary>Registers workflow settings into <paramref name="registry"/>.</summary>
	public static void Register(SettingsRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		registry.Register(new SettingDefinition {
			Key = Default,
			Kind = SettingKind.String,
			Scope = SettingScope.Workspace,
			Description = "The workflow id the new-session composer preselects, from listWorkflows; empty for no workflow. It must be "
				+ "valid and declare no inputs. Set it for one repository, or user-wide for every repository without its own.",
			Aliases = ["default workflow", "preselect workflow", "new session workflow"],
			Apply = ApplyMode.Live,
			Default = "",
		});
		registry.Register(new SettingDefinition {
			Key = DecisionConfidence,
			Kind = SettingKind.Number,
			Description = "Minimum model confidence for automatic workflow decisions, and for the new-session composer to apply its "
				+ "workflow or role-model suggestion; lower confidence asks you instead, or leaves the composer's field as it was.",
			Aliases = ["workflow confidence", "decision confidence"],
			Apply = ApplyMode.Live,
			Default = 0.8,
			Validate = value => value is double confidence && double.IsFinite(confidence) && confidence is >= 0 and <= 1
				? ValidationResult.Success : ValidationResult.Failure("must be a finite number between 0 and 1"),
		});
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
		registry.Register(new SettingDefinition {
			Key = OpenReviewItem,
			Kind = SettingKind.Bool,
			Description = "When a workflow asks you a question, have the agent open the main document it is about in a preview tab of that session.",
			Aliases = ["workflow open document", "open review document", "workflow auto open"],
			Apply = ApplyMode.Live,
			Default = true,
		});
	}
}
