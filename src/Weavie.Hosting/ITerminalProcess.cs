using Weavie.Core.Agents;

namespace Weavie.Hosting;

/// <summary>A child lifecycle driven by <see cref="TerminalController"/>.</summary>
public interface ITerminalProcess {
	/// <summary>Builds the logical launch for the next supervised start.</summary>
	AgentLaunch ResolveLaunch();

	/// <summary>Observes raw child output.</summary>
	void ObserveTerminalOutput(ReadOnlyMemory<byte> data);

	/// <summary>Observes raw child input.</summary>
	void ObserveTerminalInput(ReadOnlyMemory<byte> data);

	/// <summary>Observes a child exit before supervision applies its policy.</summary>
	void ObserveProcessExit(AgentProcessExit exit);
}
