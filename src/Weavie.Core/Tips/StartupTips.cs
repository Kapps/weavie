using Weavie.Core.Commands;

namespace Weavie.Core.Tips;

/// <summary>One startup teaching moment, with an optional command whose live shortcut the client appends.</summary>
public sealed record StartupTip {
	/// <summary>The stable identity used by tests and future catalog maintenance.</summary>
	public required string Id { get; init; }

	/// <summary>The opening instruction, ending before the optional shortcut.</summary>
	public required string Lead { get; init; }

	/// <summary>The command that supplies the tip's live shortcut, or <see langword="null"/> for a concept tip.</summary>
	public string? CommandId { get; init; }

	/// <summary>The short explanation following the opening instruction.</summary>
	public required string Detail { get; init; }
}

/// <summary>The curated catalog of distinctive Weavie workflows taught at startup.</summary>
public static class StartupTips {
	private static readonly StartupTip[] Catalog = [
		new() {
			Id = "parallel-sessions",
			Lead = "Open Sessions",
			CommandId = SessionCommands.ShowSessions,
			Detail = "Start another task in its own branch, worktree, and agent so it can run in parallel.",
		},
		new() {
			Id = "automatic-inference",
			Lead = "Enable Automatic Inference",
			CommandId = CoreCommands.EnableAutomaticInference,
			Detail = "Weavie can use LLM inferrence for features such as branch name suggestions. This will consume tokens.",
		},
		new() {
			Id = "revise-selection",
			Lead = "Run Revise Selection",
			CommandId = CoreCommands.ReviseSelection,
			Detail = "Rewrite selected code from a short instruction without adding a turn to the agent chat; the edit is one undo step.",
		},
		new() {
			Id = "review-changes",
			Lead = "Run Review Changes",
			CommandId = CoreCommands.ReviewOpen,
			Detail = "Walk every unreviewed auto-applied agent edit; Keep and Revert are both undoable.",
		},
		new() {
			Id = "learn-from-corrections",
			Lead = "Run Learn From My Corrections",
			CommandId = CoreCommands.LearnFromCorrections,
			Detail = "Turn repeated edits and reverts of agent-written lines into proposed repository rules.",
		},
		new() {
			Id = "agent-drives-weavie",
			Lead = "Ask your agent to run Weavie commands or change settings",
			Detail = "The agent can drive Weavie directly, so you do not have to hunt through menus.",
		},
		new() {
			Id = "diff-against",
			Lead = "Run Diff Against",
			CommandId = CoreCommands.DiffAgainst,
			Detail = "Review your work against any branch, tag, or commit without checking out another ref.",
		},
		new() {
			Id = "test-at-cursor",
			Lead = "Run Test at Cursor",
			CommandId = CoreCommands.RunTestAtCursor,
			Detail = "Send the repository's configured test command to the visible shell shared with your agent.",
		},
		new() {
			Id = "command-palette",
			Lead = "Open Show All Commands",
			CommandId = CoreCommands.FocusOmnibarCommands,
			Detail = "Find every available action and its current, user-overridable shortcut.",
		},
		new() {
			Id = "paste-screenshot",
			Lead = "Paste a screenshot into an agent prompt",
			CommandId = CoreCommands.AgentPaste,
			Detail = "Images work even when the agent itself is running remotely.",
		},
	];

	/// <summary>Selects one tip uniformly from the catalog with <paramref name="random"/>.</summary>
	public static StartupTip Pick(Random random) {
		ArgumentNullException.ThrowIfNull(random);
		return Catalog[random.Next(Catalog.Length)];
	}

	internal static IReadOnlyList<StartupTip> All => Catalog;
}
