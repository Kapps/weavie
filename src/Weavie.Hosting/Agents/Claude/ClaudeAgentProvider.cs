using Weavie.Core;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Claude;

/// <summary>The Claude Code provider, retaining the existing settings and conversation store.</summary>
public sealed class ClaudeAgentProvider : IAgentProvider {
	private readonly ClaudeSessionStore _sessions;

	/// <summary>Creates the provider over the app-global Claude conversation store.</summary>
	public ClaudeAgentProvider(ClaudeSessionStore sessions) {
		ArgumentNullException.ThrowIfNull(sessions);
		_sessions = sessions;
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
	};

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
