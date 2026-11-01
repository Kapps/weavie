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
			Description = "Save the plan shown in the editor and approve it with the agent's first allow option. Pass "
				+ "'requestId' to answer a named plan review instead, and 'optionId' to choose another option.",
			Aliases = ["accept plan", "approve plan", "implement plan", "start coding"],
			DefaultKeybindings = [new CommandKeybinding { Key = "$mod+Enter", When = "editorFocused && planReviewShown" }],
			ArgsSchemaJson = "{\"requestId\":{\"type\":\"string\",\"description\":\"Plan review to answer; omit for the one shown in the editor\"},"
				+ "\"optionId\":{\"type\":\"string\",\"description\":\"Permission option to answer with; omit for the first allow option\"}}",
		});

		registry.Register(new CommandDefinition {
			Id = RejectAgentPlan,
			Title = "Keep Planning",
			RunsIn = CommandLocation.Web,
			Category = "Agent",
			Description = "Decline the plan shown in the editor so the agent keeps planning. Pass 'requestId' to "
				+ "decline a named plan review instead.",
			Aliases = ["reject plan", "keep planning", "revise plan", "decline plan"],
			ArgsSchemaJson = "{\"requestId\":{\"type\":\"string\",\"description\":\"Plan review to decline; omit for the one shown in the editor\"}}",
		});
	}
}
