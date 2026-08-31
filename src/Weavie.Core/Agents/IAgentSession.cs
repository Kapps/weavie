namespace Weavie.Core.Agents;

/// <summary>The lifecycle of one provider session rooted at one Weavie worktree.</summary>
public interface IAgentSession : IAsyncDisposable {
}

/// <summary>The terminal-facing lifecycle of one provider session rooted at one Weavie worktree.</summary>
public interface ITerminalAgentSession : IAgentSession {
	/// <summary>Builds the next child launch from the provider's current conversation state.</summary>
	AgentLaunch ResolveLaunch();

	/// <summary>
	/// Sets the opening turn a not-yet-launched session starts with, for the provider to carry into its launch.
	/// Consumed by the first <see cref="ResolveLaunch"/>, so a restart never replays it. Throws once the session
	/// has launched: the opening turn is part of starting the agent, not something typed at it afterwards.
	/// </summary>
	void SeedFirstTurn(AgentTurnSubmission turn);

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

	/// <summary>Abandons the current provider conversation and starts a fresh one.</summary>
	void StartNewConversation();

	/// <summary>Selects one exact provider-advertised permission option.</summary>
	void ResolvePermission(string requestId, string optionId);

	/// <summary>Resolves a provider request for structured user input.</summary>
	void ResolveInput(
		string requestId,
		string action,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers);

	/// <summary>Starts one exact provider-advertised authentication method.</summary>
	void Authenticate(string methodId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers);

	/// <summary>Raised when the provider has a structured pane state update for the web UI.</summary>
	event Action<AgentPaneMessage> PaneMessage;

	/// <summary>Raised when provider resume supplies a complete authoritative transcript replacement.</summary>
	event Action<IReadOnlyList<AgentPaneMessage>> PaneSnapshot;
}
