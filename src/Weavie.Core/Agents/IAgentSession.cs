namespace Weavie.Core.Agents;

/// <summary>The lifecycle of one provider session rooted at one Weavie worktree.</summary>
public interface IAgentSession : IAsyncDisposable {
}

/// <summary>The terminal-facing lifecycle of one provider session rooted at one Weavie worktree.</summary>
public interface ITerminalAgentSession : IAgentSession {
	/// <summary>Builds the next child launch from the provider's current conversation state.</summary>
	AgentLaunch ResolveLaunch();

	/// <summary>Observes raw output from the current PTY child.</summary>
	void ObserveTerminalOutput(ReadOnlyMemory<byte> data);

	/// <summary>Observes raw input written to the current PTY child.</summary>
	void ObserveTerminalInput(ReadOnlyMemory<byte> data);

	/// <summary>Observes an exit before the process supervisor applies restart policy.</summary>
	void ObserveProcessExit(AgentProcessExit exit);
}

/// <summary>A native structured agent session driven by host messages rather than terminal bytes.</summary>
public interface IStructuredAgentSession : IAgentSession {
	/// <summary>Starts the structured runtime.</summary>
	void Start();

	/// <summary>Submits text and its exact staged attachments to the current provider thread.</summary>
	void Submit(AgentTurnSubmission submission);

	/// <summary>Places text in the provider's compose surface without submitting it.</summary>
	void PrefillPrompt(string prompt);

	/// <summary>Interrupts the active turn.</summary>
	void Interrupt();

	/// <summary>Restarts the structured runtime process.</summary>
	void Restart();

	/// <summary>Resolves a provider approval request.</summary>
	void ResolveApproval(string requestId, string decision);

	/// <summary>Answers a provider request for structured user input.</summary>
	void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers);

	/// <summary>Raised when the provider has a structured pane state update for the web UI.</summary>
	event Action<AgentPaneMessage> PaneMessage;
}
