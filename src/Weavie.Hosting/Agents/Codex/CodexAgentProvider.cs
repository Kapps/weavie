using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Native Codex provider identity and app-server session factory.</summary>
public sealed class CodexAgentProvider : IAgentProvider {
	private readonly CodexThreadStore _threads;

	/// <summary>Creates the native Codex provider over the app-global thread store.</summary>
	public CodexAgentProvider(CodexThreadStore threads) {
		ArgumentNullException.ThrowIfNull(threads);
		_threads = threads;
	}

	/// <inheritdoc/>
	public AgentProviderInfo Info { get; } = new() {
		Id = "codex",
		Name = "Codex",
		Capabilities = AgentProviderCapabilities.StructuredPane
			| AgentProviderCapabilities.CapabilityRegistry
			| AgentProviderCapabilities.Ide
			| AgentProviderCapabilities.Events,
		Available = true,
	};

	/// <inheritdoc/>
	public IAgentSession CreateSession(AgentSessionContext context) {
		ArgumentNullException.ThrowIfNull(context);
		string? command = context.Settings.GetString("codex.path");
		return string.IsNullOrWhiteSpace(command)
			? new UnavailableStructuredAgentSession(
				"codex",
				"Native Codex requires codex.path to point to codex.exe.",
				context.Registry)
			: new CodexAppServerSession(context, _threads, command);
	}
}
