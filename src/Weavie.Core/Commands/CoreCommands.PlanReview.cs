namespace Weavie.Core.Commands;

// Answering an agent's plan review from the editor. See docs/concepts/native-acp-agents.md.
public static partial class CoreCommands {
	/// <summary>Saves the reviewed plan and approves it (web-handled).</summary>
	public const string AcceptAgentPlan = "weavie.agent.acceptPlan";

	/// <summary>Declines the reviewed plan so the agent keeps planning (web-handled).</summary>
	public const string RejectAgentPlan = "weavie.agent.rejectPlan";

	// Registered after the diff commands so the review tab's chord answers before Keep Change declines it.
	private static void RegisterPlanReview(CommandRegistry registry) {
		registry.Register(new CommandDefinition {
			Id = AcceptAgentPlan,
			Title = "Accept Agent Plan",
			RunsIn = CommandLocation.Web,
			Category = "Agent",
			Description = "Save the plan under review and approve it with the agent's first allow option, or pass "
				+ "'optionId' to choose another. Targets the open plan tab, else the newest plan review.",
			Aliases = ["accept plan", "approve plan", "implement plan", "start coding"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Enter", When = "editorFocused && planReviewShown" }],
			When = "planReviewPending",
			ArgsSchemaJson = "{\"optionId\":{\"type\":\"string\",\"description\":\"Permission option to answer with; omit for the first allow option\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = RejectAgentPlan,
			Title = "Keep Planning",
			RunsIn = CommandLocation.Web,
			Category = "Agent",
			Description = "Decline the plan under review so the agent keeps planning. Targets the open plan tab, else "
				+ "the newest plan review.",
			Aliases = ["reject plan", "keep planning", "revise plan", "decline plan"],
			When = "planReviewPending",
		});
	}
}
