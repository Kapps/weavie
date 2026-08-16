using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>Creates native structured sessions for one immutable ACP agent profile.</summary>
public sealed class AcpAgentProvider : IAgentProvider, IAgentInferenceProvider {
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

	/// <inheritdoc/>
	/// <remarks>
	/// Both categories run the agent's own configured model. ACP exposes model and reasoning-level selectors but no
	/// cost or capability semantics for their values, so Weavie reports the agent's choice instead of overriding it.
	/// </remarks>
	public InferenceProviderInfo InferenceInfo { get; } = new() {
		Categories = [InferenceModelCategory.Utility, InferenceModelCategory.Reasoning],
	};

	/// <inheritdoc/>
	public Task<InferenceProviderResult> QueryInferenceAsync(
		InferenceProviderRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		return AcpInferenceClient.QueryAsync(_definition, request, ct);
	}
}
