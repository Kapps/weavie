namespace Weavie.Core.Agents;

/// <summary>The terminal-facing lifecycle of one provider session rooted at one Weavie worktree.</summary>
public interface IAgentSession : IAsyncDisposable {
	/// <summary>Builds the next child launch from the provider's current conversation state.</summary>
	AgentLaunch ResolveLaunch();

	/// <summary>Observes raw output from the current PTY child.</summary>
	void ObserveTerminalOutput(ReadOnlyMemory<byte> data);

	/// <summary>Observes raw input written to the current PTY child.</summary>
	void ObserveTerminalInput(ReadOnlyMemory<byte> data);

	/// <summary>Observes an exit before the process supervisor applies restart policy.</summary>
	void ObserveProcessExit(AgentProcessExit exit);
}
