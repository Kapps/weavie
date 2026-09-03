using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

/// <summary>Creates native structured sessions for one immutable ACP agent profile.</summary>
public sealed class AcpAgentProvider : IAgentProvider, IAgentInferenceProvider {
	private readonly AcpAgentDefinition _definition;
	private readonly AcpSessionStore _sessions;
	private readonly AcpControlStore _controls;
	private readonly Action<string> _log;

	/// <summary>Creates a provider for <paramref name="definition"/>.</summary>
	public AcpAgentProvider(AcpAgentDefinition definition, AcpSessionStore sessions, AcpControlStore controls, Action<string> log) {
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(controls);
		ArgumentNullException.ThrowIfNull(log);
		_definition = definition;
		_sessions = sessions;
		_controls = controls;
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
		new AcpAgentSession(context, _definition, _sessions, _controls, _log);

	/// <inheritdoc/>
	/// <remarks>
	/// Explicit inference profile values select exact provider-advertised controls. Blank values retain the agent's
	/// defaults because ACP does not assign portable cost or capability semantics to provider-native values.
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
