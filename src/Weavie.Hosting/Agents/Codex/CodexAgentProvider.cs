using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Codex;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Native Codex provider identity and app-server session factory.</summary>
public sealed class CodexAgentProvider : IAgentInferenceProvider {
	private readonly CodexThreadStore _threads;
	private readonly IInferenceProvider _inference;
	private readonly Func<AgentSessionContext, CodexThreadStore, CodexCliLaunch, IAgentSession> _createSession;

	/// <summary>Creates the native Codex provider over the app-global thread store.</summary>
	public CodexAgentProvider(SettingsStore settings, CodexThreadStore threads)
		: this(threads, new CodexCliInference(settings, new AgentCliProcessRunner()), CreateCodexSession) {
	}

	internal CodexAgentProvider(
		CodexThreadStore threads,
		IInferenceProvider inference,
		Func<AgentSessionContext, CodexThreadStore, CodexCliLaunch, IAgentSession> createSession) {
		ArgumentNullException.ThrowIfNull(threads);
		ArgumentNullException.ThrowIfNull(inference);
		ArgumentNullException.ThrowIfNull(createSession);
		_threads = threads;
		_inference = inference;
		_createSession = createSession;
	}

	/// <inheritdoc/>
	public AgentProviderInfo Info { get; } = new() {
		Id = "codex",
		Name = "Codex (WIP)",
		Capabilities = AgentProviderCapabilities.StructuredPane
			| AgentProviderCapabilities.CapabilityRegistry
			| AgentProviderCapabilities.Ide
			| AgentProviderCapabilities.Events,
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
		string? command = context.Settings.GetString(CoreSettings.CodexPath);
		if (string.IsNullOrWhiteSpace(command)) {
			return new UnavailableStructuredAgentSession(
				"codex",
				CodexUnavailableMessages.SettingsFix(
					"Native Codex could not find an auto-detected Codex install.",
					context.Settings.FilePath),
				context.Registry);
		}

		try {
			var launch = CodexInstallResolver.Resolve(command, context.Workspace);
			return _createSession(context, _threads, launch);
		} catch (InvalidOperationException ex) {
			return new UnavailableStructuredAgentSession("codex", CodexUnavailableMessages.SettingsFix(ex.Message, context.Settings.FilePath), context.Registry);
		}
	}

	private static IAgentSession CreateCodexSession(AgentSessionContext context, CodexThreadStore threads, CodexCliLaunch launch) =>
		new CodexAppServerSession(context, threads, launch);
}
