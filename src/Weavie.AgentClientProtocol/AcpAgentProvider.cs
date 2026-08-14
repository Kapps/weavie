using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>Creates native structured sessions for one immutable ACP agent profile.</summary>
public sealed class AcpAgentProvider : IAgentProvider {
	private readonly AcpAgentDefinition _definition;
	private readonly AcpSessionStore _sessions;
	private readonly Action<string> _log;

	/// <summary>Creates a provider for <paramref name="definition"/>.</summary>
	public AcpAgentProvider(AcpAgentDefinition definition, AcpSessionStore sessions, Action<string> log) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(log);
		_definition = definition;
		_sessions = sessions;
		_log = log;
		Info = new AgentProviderInfo {
			Id = definition.Id,
			Name = definition.Name,
			Capabilities = AgentProviderCapabilities.StructuredPane
				| AgentProviderCapabilities.CapabilityRegistry
				| AgentProviderCapabilities.Ide
				| AgentProviderCapabilities.Events
				| AgentProviderCapabilities.EditDisposition,
			Available = true,
			UnavailableReason = null,
		};
	}

	/// <inheritdoc/>
	public AgentProviderInfo Info { get; }

	/// <inheritdoc/>
	public IAgentSession CreateSession(AgentSessionContext context) =>
		new AcpAgentSession(context, _definition, _sessions, _log);
}
