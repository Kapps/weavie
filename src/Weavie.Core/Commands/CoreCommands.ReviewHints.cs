namespace Weavie.Core.Commands;

// Inline review hints and the shared Fix action. See docs/specs/inline-review-hints.md.
public static partial class CoreCommands {
	/// <summary>Moves to the next review hint across files and opens it (web-handled).</summary>
	public const string ReviewHintsNext = "weavie.reviewHints.next";

	/// <summary>Moves to the previous review hint across files and opens it (web-handled).</summary>
	public const string ReviewHintsPrevious = "weavie.reviewHints.previous";

	/// <summary>Shows or hides review hints in every editor (web-handled).</summary>
	public const string ReviewHintsToggle = "weavie.reviewHints.toggle";

	/// <summary>Dismisses the open review hint, else the one on the cursor line (web-handled).</summary>
	public const string ReviewHintsDismiss = "weavie.reviewHints.dismiss";

	/// <summary>Sends the open hint, else the cursor line's hint or PR thread, to the agent to fix (web-handled).</summary>
	public const string ReviewHintsFix = "weavie.reviewHints.fix";

	/// <summary>As <see cref="ReviewHintsFix"/>, with the user's own instruction (web-handled).</summary>
	public const string ReviewHintsFixWith = "weavie.reviewHints.fixWith";

	/// <summary>Closes the open review hint popover; bound to <c>Escape</c> while it is open (web-handled).</summary>
	public const string ReviewHintsClose = "weavie.reviewHints.close";

	/// <summary>Removes every review hint in the session.</summary>
	public const string ReviewHintsClear = "weavie.reviewHints.clear";

	/// <summary>Submits a fix prompt for one line-anchored finding to the session's agent.</summary>
	public const string AgentFixLine = "weavie.agent.fixLine";

	private static void RegisterReviewHints(CommandRegistry registry) {
		foreach (var (id, title, direction) in new[] {
			(ReviewHintsNext, "Next Review Hint", "next"),
			(ReviewHintsPrevious, "Previous Review Hint", "previous"),
		}) {
			registry.Register(new CommandDefinition {
				Id = id,
				Title = title,
				RunsIn = CommandLocation.Web,
				Category = "Review",
				Description = $"Go to the {direction} agent review hint, across files, and open it.",
				Aliases = [$"{direction} hint", $"{direction} review hint", $"{direction} finding"],
			});
		}

		registry.Register(new CommandDefinition {
			Id = ReviewHintsToggle,
			Title = "Toggle Review Hints",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Show or hide agent review hints in every editor.",
			Aliases = ["toggle hints", "show review hints", "hide review hints", "toggle findings"],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewHintsDismiss,
			Title = "Dismiss Review Hint",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Dismiss the open review hint, or the one on the cursor's line.",
			Aliases = ["dismiss hint", "remove hint", "dismiss finding"],
		});

		foreach (var (id, title, description) in new[] {
			(ReviewHintsFix, "Fix Review Hint", "Ask the agent to fix the open review hint, or the hint or pull request thread on the cursor's line."),
			(ReviewHintsFixWith, "Fix Review Hint With…", "Ask the agent to fix the open review hint, or the hint or pull request thread on the cursor's line, with your own instruction."),
		}) {
			registry.Register(new CommandDefinition {
				Id = id,
				Title = title,
				RunsIn = CommandLocation.Web,
				Category = "Review",
				Description = description,
				Aliases = ["fix hint", "fix finding", "fix this", "fix comment", "fix pr comment"],
			});
		}

		registry.Register(new CommandDefinition {
			Id = ReviewHintsClose,
			Title = "Close Review Hint",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Close the open review hint popover.",
			DefaultKeybindings = [new CommandKeybinding { Key = "Escape", When = "reviewHintOpen && editorFocused" }],
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = ReviewHintsClear,
			Title = "Clear Review Hints",
			RunsIn = CommandLocation.Core,
			Category = "Review",
			Description = "Remove every agent review hint in this session.",
			Aliases = ["clear hints", "remove all hints", "clear findings", "clear review hints"],
		});

		registry.Register(new CommandDefinition {
			Id = AgentFixLine,
			Title = "Fix Line",
			RunsIn = CommandLocation.Core,
			Category = "Agent",
			Description = "Submit a prompt asking this session's agent to fix one finding at a file line.",
			ShowInPalette = false,
			ArgsSchemaJson = """{"path":{"type":"string","description":"Absolute path of the file"},"line":{"type":"integer","minimum":1,"description":"1-based line"},"quote":{"type":"string","description":"The line's text"},"text":{"type":"string","description":"The finding"},"instruction":{"type":"string","description":"The user's own instruction, or empty"}}""",
		});
	}
}
