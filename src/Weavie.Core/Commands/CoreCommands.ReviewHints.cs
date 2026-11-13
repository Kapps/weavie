namespace Weavie.Core.Commands;

// Inline review hints and the shared Fix action. See docs/specs/inline-review-hints.md.
public static partial class CoreCommands {
	/// <summary>Goes to the next PR thread or review hint across files; bound to <c>$mod+alt+]</c> (web-handled).</summary>
	public const string ReviewNextItem = "weavie.review.nextItem";

	/// <summary>Goes to the previous PR thread or review hint across files; bound to <c>$mod+alt+[</c> (web-handled).</summary>
	public const string ReviewPreviousItem = "weavie.review.previousItem";

	/// <summary>Includes or skips resolved and outdated threads when stepping through comments and hints.</summary>
	public const string ReviewToggleSettledItems = "weavie.review.toggleSettledItems";

	/// <summary>Shows or hides review hints in every editor.</summary>
	public const string ReviewHintsToggle = "weavie.reviewHints.toggle";

	/// <summary>Dismisses the open review hint, else the one on the cursor line (web-handled).</summary>
	public const string ReviewHintsDismiss = "weavie.reviewHints.dismiss";

	/// <summary>Sends the open hint, else the cursor line's hint or PR thread, to the agent to fix (web-handled).</summary>
	public const string ReviewHintsFix = "weavie.reviewHints.fix";

	/// <summary>As <see cref="ReviewHintsFix"/>, with the user's own instruction (web-handled).</summary>
	public const string ReviewHintsFixWith = "weavie.reviewHints.fixWith";

	/// <summary>Opens a pull request comment on the open hint's line, prefilled with its text, for the user to edit and post (web-handled).</summary>
	public const string ReviewHintsComment = "weavie.reviewHints.comment";

	/// <summary>Closes the open review hint popover; bound to <c>Escape</c> after the editor's own widgets decline it (web-handled).</summary>
	public const string ReviewHintsClose = "weavie.reviewHints.close";

	/// <summary>Removes every review hint in the session.</summary>
	public const string ReviewHintsClear = "weavie.reviewHints.clear";

	/// <summary>Submits a fix prompt for one line-anchored finding to the session's agent.</summary>
	public const string AgentFixLine = "weavie.agent.fixLine";

	private static void RegisterReviewHints(CommandRegistry registry) {
		foreach (var (id, title, direction, key) in new[] {
			(ReviewNextItem, "Next Comment or Hint", "next", "$mod+alt+]"),
			(ReviewPreviousItem, "Previous Comment or Hint", "previous", "$mod+alt+["),
		}) {
			registry.Register(new CommandDefinition {
				Id = id,
				Title = title,
				RunsIn = CommandLocation.Web,
				Category = "Review",
				When = "reviewItemsAvailable",
				Description = $"Go to the {direction} pull request comment or agent review hint, across files.",
				Aliases = [$"{direction} comment", $"{direction} pr comment", $"{direction} review comment", $"{direction} thread",
					$"{direction} hint", $"{direction} review hint", $"{direction} finding"],
				DefaultKeybindings = [new CommandKeybinding { Key = key, When = "reviewItemsAvailable && editorFocused" }],
			});
		}

		registry.Register(new CommandDefinition {
			Id = ReviewToggleSettledItems,
			Title = "Toggle Resolved and Outdated in Stepping",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Owner = CommandOwner.Client,
			Category = "Review",
			Description = "Include or skip resolved and outdated pull request threads in Next Comment or Hint "
				+ "(the 'editor.reviewStepSettled' setting).",
			ArgsSchemaJson = Configuration.ReviewItemCommands.ArgsSchemaJson,
			Aliases = ["resolved comments", "outdated comments", "step resolved threads"],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewHintsToggle,
			Title = "Toggle Review Hints",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Owner = CommandOwner.Client,
			Category = "Review",
			Description = "Show or hide agent review hints in every editor, and in Next Comment or Hint "
				+ "(the 'editor.reviewHints' setting).",
			ArgsSchemaJson = Configuration.ReviewItemCommands.ArgsSchemaJson,
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
			Id = ReviewHintsComment,
			Title = "Comment on PR From Review Hint",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Open a pull request comment on the open review hint's line, or the cursor line's hint, prefilled with the hint's text for you to edit and post as yourself.",
			Aliases = ["comment from hint", "post hint", "hint to pr comment", "promote hint"],
		});

		registry.Register(new CommandDefinition {
			Id = ReviewHintsClose,
			Title = "Close Review Hint",
			RunsIn = CommandLocation.Web,
			Category = "Review",
			Description = "Close the open review hint popover.",
			DefaultKeybindings = [new CommandKeybinding { Key = "Escape", When = "editorFocused" }],
			ShowInPalette = false,
		});

		registry.Register(new CommandDefinition {
			Id = ReviewHintsClear,
			Title = "Clear Review Hints",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Category = "Review",
			Description = "Remove every agent review hint in this session.",
			Aliases = ["clear hints", "remove all hints", "clear findings", "clear review hints"],
		});

		registry.Register(new CommandDefinition {
			Id = AgentFixLine,
			Title = "Fix Line",
			RunsIn = CommandLocation.Core,
			OnTimeout = OperationSeverity.ActionFailed,
			Category = "Agent",
			Description = "Submit a prompt asking this session's agent to fix one finding at a file line.",
			ShowInPalette = false,
			ArgsSchemaJson = """{"path":{"type":"string","description":"Absolute path of the file"},"line":{"type":"integer","minimum":1,"description":"1-based line"},"quote":{"type":"string","description":"The line's text"},"text":{"type":"string","description":"The finding"},"instruction":{"type":"string","description":"The user's own instruction, or empty"}}""",
		});
	}
}
