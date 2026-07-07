using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Native Codex provider identity and app-server session factory.</summary>
public sealed class CodexAgentProvider : IAgentProvider {
	private readonly CodexThreadStore _threads;
	private readonly Func<AgentSessionContext, CodexThreadStore, string, IAgentSession> _createSession;

	/// <summary>Creates the native Codex provider over the app-global thread store.</summary>
	public CodexAgentProvider(CodexThreadStore threads)
		: this(threads, CreateCodexSession) {
	}

	internal CodexAgentProvider(
		CodexThreadStore threads,
		Func<AgentSessionContext, CodexThreadStore, string, IAgentSession> createSession) {
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentNullException.ThrowIfNull(createSession);
		_threads = threads;
		_createSession = createSession;
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
		if (string.IsNullOrWhiteSpace(command)) {
			return new UnavailableStructuredAgentSession(
				"codex",
				"Native Codex requires codex.path to point to codex.exe.",
				context.Registry);
		}

		try {
			return _createSession(context, _threads, command);
		} catch (InvalidOperationException ex) {
			return new UnavailableStructuredAgentSession("codex", ex.Message, context.Registry);
		}
	}

	private static IAgentSession CreateCodexSession(AgentSessionContext context, CodexThreadStore threads, string command) =>
		new CodexAppServerSession(context, threads, command);
}
