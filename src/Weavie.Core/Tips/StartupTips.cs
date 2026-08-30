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
			Lead = "Start another task from Sessions",
			CommandId = SessionCommands.ShowSessions,
			Detail = "It gets its own branch, worktree, and agent, so both tasks can keep running.",
		},
		new() {
			Id = "repo-aware-branch-names",
			Lead = "Describe a task in Sessions",
			CommandId = SessionCommands.ShowSessions,
			Detail = "With Automatic Inference enabled, Weavie suggests a branch name that follows the repository's conventions.",
		},
		new() {
			Id = "revise-selection",
			Lead = "Select code, then run Revise Selection",
			CommandId = CoreCommands.ReviseSelection,
			Detail = "It rewrites in place from a short instruction without adding a chat turn, and the whole edit is one undo step.",
		},
		new() {
			Id = "review-changes",
			Lead = "Review auto-applied changes",
			CommandId = CoreCommands.ReviewOpen,
			Detail = "Unreviewed agent edits accumulate across turns; Keep and Revert are both undoable.",
		},
		new() {
			Id = "learn-from-corrections",
			Lead = "Run Learn From My Corrections",
			CommandId = CoreCommands.LearnFromCorrections,
			Detail = "Weavie remembers edits and reverts over agent-written lines and can turn repeated fixes into repository rules.",
		},
		new() {
			Id = "agent-drives-weavie",
			Lead = "Ask your agent to change Weavie itself",
			Detail = "It can run Weavie commands and change settings directly from the chat.",
		},
		new() {
			Id = "diff-against",
			Lead = "Run Diff Against",
			CommandId = CoreCommands.DiffAgainst,
			Detail = "Review your work against any branch, tag, or commit without checking out another ref.",
		},
		new() {
			Id = "git-blame-history",
			Lead = "Run Show Blame for This Line",
			CommandId = CoreCommands.ShowBlame,
			Detail = "Open the commit hunk, pull request, and line or file history behind the current line.",
		},
		new() {
			Id = "pull-request-session",
			Lead = "Open a pull request as a session",
			CommandId = SessionCommands.OpenPr,
			Detail = "Weavie checks out its head branch in a separate worktree and gives the agent its review context.",
		},
		new() {
			Id = "paste-screenshot",
			Lead = "Paste a screenshot straight into an agent prompt",
			CommandId = CoreCommands.AgentPaste,
			Detail = "Weavie transfers the image to the owning session even when its agent runs remotely.",
		},
	];

	/// <summary>Selects one tip uniformly from the catalog with <paramref name="random"/>.</summary>
	public static StartupTip Pick(Random random) {
		ArgumentNullException.ThrowIfNull(random);
		return Catalog[random.Next(Catalog.Length)];
	}

	internal static IReadOnlyList<StartupTip> All => Catalog;
}
