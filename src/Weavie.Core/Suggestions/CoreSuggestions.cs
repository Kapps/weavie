using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Inference.Decisions;

namespace Weavie.Core.Suggestions;

/// <summary>
/// The built-in suggestions, registered into a <see cref="SuggestionRegistry"/> at startup.
/// </summary>
public static class CoreSuggestions {
	/// <summary>How many agent-answered workflow decisions make the decision-model card relevant.</summary>
	public const int DecisionModelThreshold = 5;

	/// <summary>The agent's median decision time at or above which a decision model (~0.2s) is meaningfully faster.</summary>
	public static readonly TimeSpan DecisionModelSlowAgentMedian = TimeSpan.FromSeconds(1);

	/// <summary>Builds a registry pre-loaded with the built-in suggestions.</summary>
	public static SuggestionRegistry CreateRegistry() {
		var registry = new SuggestionRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Registers the built-in suggestions into <paramref name="registry"/>.</summary>
	public static void Register(SuggestionRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		// Offer to set the workspace up when the repo looks like it needs it (a build manifest is present) and a
		// knowledge-shaped setting is still unconfigured — the worktree setup command OR the test profile. "Yes"
		// engages Claude in the invoking session to configure both. Supersedes the old worktree-only card, whose
		// "don't ask again" carries over via LegacyIds.
		registry.Register(new SuggestionDefinition {
			Id = "workspace.setup",
			Title = "Set up this workspace?",
			Body = _ => "Claude can configure how to prepare a fresh checkout and how to run this repo's tests.",
			LegacyIds = [CoreSettings.WorktreeSetupCommand],
			IsRelevant = ctx => ctx.HasBuildManifest && (
				string.IsNullOrWhiteSpace(ctx.Settings.RequireString(CoreSettings.WorktreeSetupCommand, ctx.WorkspaceRoot)) ||
				string.IsNullOrWhiteSpace(ctx.Settings.RequireString(TestSettings.Profile, ctx.WorkspaceRoot))),
			Actions = [
				new SuggestionAction {
					Label = "Yes",
					Kind = SuggestionActionKind.RunCommand,
					CommandId = CoreCommands.SetupWorkspace,
				},
				new SuggestionAction { Label = "Not now", Kind = SuggestionActionKind.Snooze },
				new SuggestionAction { Label = "Don't ask again", Kind = SuggestionActionKind.DismissForever },
			],
		});

		// Offer /learn once enough post-turn corrections (reverted hunks / hand-edits over agent output)
		// accumulate in the workspace ring. Self-regulating: /learn clears the ring, so the card vanishes
		// until corrections build up again. "Yes" only PREFILLS the analysis prompt — no tokens until Enter.
		registry.Register(new SuggestionDefinition {
			Id = "corrections.learn",
			Title = "Teach Claude from your corrections?",
			Body = _ => "You've been correcting Claude's output — it can mine those reverts and edits for AGENTS.md rules.",
			IsRelevant = ctx => ctx.Settings.RequireBool(CorrectionsSettings.Enabled)
				&& ctx.Signals.PendingCorrectionCount >= ctx.Settings.RequireInt(CorrectionsSettings.LearnThreshold),
			Actions = [
				new SuggestionAction {
					Label = "Yes",
					Kind = SuggestionActionKind.RunCommand,
					CommandId = CoreCommands.LearnFromCorrections,
				},
				new SuggestionAction { Label = "Not now", Kind = SuggestionActionKind.Snooze },
				new SuggestionAction { Label = "Don't ask again", Kind = SuggestionActionKind.DismissForever },
			],
		});

		registry.Register(new SuggestionDefinition {
			Id = "decisions.model",
			Title = "Faster workflow decisions?",
			Body = ctx => FormattableString.Invariant(
				$"Your last {ctx.Signals.WorkflowDecisions.AgentAnswered} workflow decisions took ~{ctx.Signals.WorkflowDecisions.AgentMedian.TotalSeconds:0.0}s each through the agent. A decision model answers in ~0.2s with calibrated confidence."),
			IsRelevant = ctx => ctx.Settings.RequireString(InferenceSettings.DecisionProvider) == DecisionProviderIds.Agent
				&& ctx.Signals.WorkflowDecisions.AgentAnswered >= DecisionModelThreshold
				&& ctx.Signals.WorkflowDecisions.AgentMedian >= DecisionModelSlowAgentMedian,
			Actions = [
				new SuggestionAction {
					Label = "Choose a decision model",
					Kind = SuggestionActionKind.RunCommand,
					CommandId = CoreCommands.ChooseDecisionProvider,
				},
				new SuggestionAction { Label = "Not now", Kind = SuggestionActionKind.Snooze },
				new SuggestionAction { Label = "Don't ask again", Kind = SuggestionActionKind.DismissForever },
			],
		});
	}
}
