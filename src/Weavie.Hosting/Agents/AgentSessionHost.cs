using Weavie.Core.Agents;
using Weavie.Core.Configuration;

namespace Weavie.Hosting.Agents;

/// <summary>Composes one provider session with Weavie's generic supervised terminal.</summary>
public sealed class AgentSessionHost : IAsyncDisposable {
	/// <summary>Creates the provider session and its existing <c>claude</c> compatibility pane.</summary>
	public AgentSessionHost(
		IAgentProvider provider,
		AgentSessionContext context,
		IHostBridge bridge,
		SettingsStore settings,
		IPtyLauncher ptyLauncher) {
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(ptyLauncher);
		Provider = provider.Info;
		Session = provider.CreateSession(context);
		Terminal = new TerminalController(bridge, "claude", settings, ptyLauncher, new AgentTerminalProcess(Session)) {
			Workspace = context.Workspace,
		};
	}

	/// <summary>The selected provider identity.</summary>
	public AgentProviderInfo Provider { get; }

	/// <summary>The live provider session.</summary>
	public IAgentSession Session { get; }

	/// <summary>The provider's compatibility terminal pane.</summary>
	public TerminalController Terminal { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		Terminal.Dispose();
		await DisposeProviderAsync().ConfigureAwait(false);
	}

	/// <summary>Disposes provider integration after the terminal has already stopped.</summary>
	public ValueTask DisposeProviderAsync() => Session.DisposeAsync();

	private sealed class AgentTerminalProcess(IAgentSession session) : ITerminalProcess {
		public AgentLaunch ResolveLaunch() => session.ResolveLaunch();

		public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) => session.ObserveTerminalOutput(data);

		public void ObserveTerminalInput(ReadOnlyMemory<byte> data) => session.ObserveTerminalInput(data);

		public void ObserveProcessExit(AgentProcessExit exit) => session.ObserveProcessExit(exit);
	}
}
