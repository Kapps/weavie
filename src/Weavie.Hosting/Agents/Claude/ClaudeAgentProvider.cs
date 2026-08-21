using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Claude;

namespace Weavie.Hosting.Agents.Claude;

/// <summary>The Claude Code provider, retaining the existing settings and conversation store.</summary>
public sealed class ClaudeAgentProvider : IAgentInferenceProvider {
	private readonly ClaudeSessionStore _sessions;
	private readonly IInferenceProvider _inference;

	/// <summary>Creates the provider over the app-global Claude conversation store.</summary>
	public ClaudeAgentProvider(SettingsStore settings, ClaudeSessionStore sessions)
		: this(
			sessions,
			new ClaudeCliInference(
				settings,
				new AgentCliProcessRunner(),
				WeaviePaths.Internal("inference-images"))) { }

	internal ClaudeAgentProvider(ClaudeSessionStore sessions, IInferenceProvider inference) {
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(inference);
		_sessions = sessions;
		_inference = inference;
	}

	/// <inheritdoc/>
	public AgentProviderInfo Info { get; } = new() {
		Id = "claude",
		Name = "Claude Code",
		Capabilities = AgentProviderCapabilities.Terminal
			| AgentProviderCapabilities.CapabilityRegistry
			| AgentProviderCapabilities.Ide
			| AgentProviderCapabilities.Events
			| AgentProviderCapabilities.EditDisposition,
		Available = true,
	};

	/// <inheritdoc/>
	public InferenceProviderInfo InferenceInfo => _inference.InferenceInfo;

	/// <inheritdoc/>
	public Task<InferenceProviderResult> QueryInferenceAsync(InferenceProviderRequest request, CancellationToken ct) =>
		_inference.QueryInferenceAsync(request, ct);

	/// <inheritdoc/>
	public IAgentSession CreateSession(AgentSessionContext context) {
		ArgumentNullException.ThrowIfNull(context);
		return new ClaudeAgentSession(
			context.Settings,
			context.Workspace,
			_sessions,
			new ClaudeTranscripts(context.FileSystem, ClaudeConfigPaths.ProjectsDirectory),
			context.Registry,
			context.DiffPresenter,
			context.Editor,
			context.Runtime,
			context.Events);
	}
}
