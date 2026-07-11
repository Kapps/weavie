using Weavie.Core.Processes;

namespace Weavie.Core.Agents;

/// <summary>A provider-neutral fact observed from an agent session.</summary>
public abstract record AgentEvent;

/// <summary>The agent session started or restarted.</summary>
public sealed record AgentSessionStarted(string? Source) : AgentEvent;

/// <summary>The provider's supervised runtime process changed state.</summary>
public sealed record AgentProcessChanged(SupervisorStateChanged Change) : AgentEvent;

/// <summary>A user prompt entered the agentic loop; <paramref name="Prompt"/> is its text when the provider reports one.</summary>
public sealed record AgentPromptSubmitted(string? SessionId, string? Prompt) : AgentEvent;

/// <summary>The agent turn stopped, optionally with a pending self-resumption.</summary>
public sealed record AgentTurnStopped(bool WillResume) : AgentEvent;

/// <summary>The agent emitted a user-facing notification.</summary>
public sealed record AgentNotification(string? Message) : AgentEvent;

/// <summary>The agent is requesting permission for a tool invocation.</summary>
public sealed record AgentPermissionRequested : AgentEvent;

/// <summary>The provider resolved a permission request.</summary>
public sealed record AgentPermissionResolved(bool RequiresUserInput) : AgentEvent;

/// <summary>A provider reported its current edit disposition.</summary>
public sealed record AgentEditDispositionObserved(string Disposition) : AgentEvent;

/// <summary>A normalized file mutation associated with a tool invocation.</summary>
public abstract record AgentMutation {
	private AgentMutation() { }

	/// <summary>The tool is not a recognized direct file mutation.</summary>
	public sealed record None : AgentMutation;

	/// <summary>The tool may mutate files, but the provider did not give per-file paths before it runs.</summary>
	public sealed record Workspace(string InvocationId) : AgentMutation;

	/// <summary>The tool directly mutates <paramref name="Path"/>, resolved relative to <paramref name="Cwd"/>.</summary>
	public sealed record File(string Path, string? Cwd, bool ProvidesEditLocation) : AgentMutation;

	/// <summary>The tool directly mutates multiple files.</summary>
	public sealed record Files(IReadOnlyList<File> Items) : AgentMutation;
}

/// <summary>A tool is about to run.</summary>
public sealed record AgentToolStarting(AgentMutation Mutation) : AgentEvent;

/// <summary>A tool finished running.</summary>
public sealed record AgentToolCompleted(AgentMutation Mutation) : AgentEvent;

/// <summary>An event with no shared Weavie behavior.</summary>
public sealed record AgentOtherEvent : AgentEvent;

/// <summary>Synchronous feedback produced while shared consumers observe one agent event.</summary>
public sealed record AgentEventFeedback {
	/// <summary>No feedback for the provider.</summary>
	public static AgentEventFeedback None { get; } = new() { Messages = [] };

	/// <summary>Provider-facing messages added to the current event response.</summary>
	public required IReadOnlyList<string> Messages { get; init; }
}
