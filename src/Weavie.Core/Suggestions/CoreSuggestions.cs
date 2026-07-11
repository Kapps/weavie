using Weavie.Core.Commands;
using Weavie.Core.Configuration;

namespace Weavie.Core.Suggestions;

/// <summary>
/// The built-in suggestions, registered into a <see cref="SuggestionRegistry"/> at startup.
/// </summary>
public static class CoreSuggestions {
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
		// engages Claude in the primary session to configure both. Supersedes the old worktree-only card, whose
		// "don't ask again" carries over via LegacyIds.
		registry.Register(new SuggestionDefinition {
			Id = "workspace.setup",
			Title = "Set up this workspace?",
			Body = "Claude can configure how to prepare a fresh checkout and how to run this repo's tests.",
			LegacyIds = ["worktree.setupCommand"],
			IsRelevant = ctx => ctx.HasBuildManifest && (
				string.IsNullOrWhiteSpace(ctx.Settings.GetString("worktree.setupCommand", ctx.WorkspaceRoot)) ||
				string.IsNullOrWhiteSpace(ctx.Settings.GetString(TestSettings.Profile, ctx.WorkspaceRoot))),
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
			Body = "You've been correcting Claude's output — it can mine those reverts and edits for CLAUDE.md rules.",
			IsRelevant = ctx => ctx.PendingCorrectionCount >= ctx.Settings.RequireInt(CorrectionsSettings.LearnThreshold),
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
	}
}
