using Weavie.Core.Commands;

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

		// Offer to configure worktree.setupCommand when the repo looks like it needs one (a build manifest is
		// present) and the setting is still empty. "Yes" engages Claude in the primary session.
		registry.Register(new SuggestionDefinition {
			Id = "worktree.setupCommand",
			Title = "Set up new worktrees automatically?",
			Body = "Claude can pick a command (e.g. install dependencies) to run whenever you create a worktree.",
			IsRelevant = ctx =>
				string.IsNullOrWhiteSpace(ctx.Settings.GetString("worktree.setupCommand")) && ctx.HasBuildManifest,
			Actions = [
				new SuggestionAction {
					Label = "Yes",
					Kind = SuggestionActionKind.RunCommand,
					CommandId = CoreCommands.SuggestSetupCommand,
				},
				new SuggestionAction { Label = "Not now", Kind = SuggestionActionKind.Snooze },
				new SuggestionAction { Label = "Don't ask again", Kind = SuggestionActionKind.DismissForever },
			],
		});
	}
}
