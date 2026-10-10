namespace Weavie.Core.Agents;

/// <summary>Whether a background item is a subagent conversation or an agent-owned task.</summary>
public enum AgentBackgroundKind {
	/// <summary>A read-only subagent conversation the agent spawned.</summary>
	Subagent,

	/// <summary>A background task such as a shell command or a workflow.</summary>
	Task,
}

/// <summary>The liveness of one background item; a later terminal state replaces an earlier one.</summary>
public enum AgentBackgroundState {
	/// <summary>The work is running.</summary>
	Running,

	/// <summary>The work is paused and may resume.</summary>
	Paused,

	/// <summary>The work finished.</summary>
	Completed,

	/// <summary>The work failed.</summary>
	Failed,

	/// <summary>The work was stopped or cancelled.</summary>
	Stopped,
}

/// <summary>What a background task reported consuming so far.</summary>
public sealed record AgentBackgroundUsage(long? TotalTokens, long? ToolUses, long? DurationMs);

/// <summary>One subagent or background task owned by a structured agent session.</summary>
public sealed record AgentBackgroundItem {
	/// <summary>The session-unique item id: a subagent's conversation id, or <c>task:&lt;provider id&gt;</c>.</summary>
	public required string Id { get; init; }

	/// <summary>Whether this is a subagent or a task.</summary>
	public required AgentBackgroundKind Kind { get; init; }

	/// <summary>The user-facing name.</summary>
	public required string Name { get; init; }

	/// <summary>The provider-reported work type, such as <c>subagent</c>, <c>shell</c>, or <c>workflow</c>.</summary>
	public required string Type { get; init; }

	/// <summary>A longer description of the work, when reported.</summary>
	public required string? Detail { get; init; }

	/// <summary>The latest activity reported, such as the last tool used or a summary.</summary>
	public required string? LastActivity { get; init; }

	/// <summary>Usage reported by a task, when any.</summary>
	public required AgentBackgroundUsage? Usage { get; init; }

	/// <summary>The current liveness.</summary>
	public required AgentBackgroundState State { get; init; }

	/// <summary>Whether the user can stop this item now.</summary>
	public required bool CanStop { get; init; }

	/// <summary>When the work started, as Unix milliseconds.</summary>
	public required long StartedAtMs { get; init; }

	/// <summary>When the work reached a terminal state, as Unix milliseconds.</summary>
	public required long? EndedAtMs { get; init; }

	/// <summary>The transcript item that renders this work, when it has a card.</summary>
	public required string? TranscriptItemId { get; init; }

	/// <summary>Whether the work is still live.</summary>
	public bool Running => State is AgentBackgroundState.Running or AgentBackgroundState.Paused;
}
