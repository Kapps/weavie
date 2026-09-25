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
	private readonly SettingsStore _settings;
	private readonly ClaudeSessionStore _sessions;
	private readonly IInferenceProvider _inference;

	/// <summary>Creates the provider over the app-global Claude conversation store.</summary>
	public ClaudeAgentProvider(SettingsStore settings, ClaudeSessionStore sessions)
		: this(
			settings,
			sessions,
			new ClaudeCliInference(
				settings,
				new AgentCliProcessRunner(),
				WeaviePaths.Internal("inference-images"))) { }

	internal ClaudeAgentProvider(SettingsStore settings, ClaudeSessionStore sessions, IInferenceProvider inference) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(inference);
		_settings = settings;
		_sessions = sessions;
		_inference = inference;
	}

	/// <inheritdoc/>
	public AgentProviderInfo Info {
		get {
			string command = _settings.RequireString(CoreSettings.ClaudePath);
			bool installed = ExecutableFinder.FindOnPath(command) is not null;
			return new() {
				Id = "claude",
				Name = "Claude Code",
				Capabilities = AgentProviderCapabilities.Terminal
					| AgentProviderCapabilities.CapabilityRegistry
					| AgentProviderCapabilities.Ide
					| AgentProviderCapabilities.Events
					| AgentProviderCapabilities.EditDisposition,
				Available = installed,
				UnavailableReason = installed
					? null
					: $"Claude Code isn't installed: '{command}' was not found. Install it, or set claude.path to its location.",
			};
		}
	}

	/// <inheritdoc/>
	public InferenceProviderInfo InferenceInfo => _inference.InferenceInfo;

	/// <inheritdoc/>
	public Task<InferenceProviderResult> QueryInferenceAsync(InferenceProviderRequest request, CancellationToken ct) =>
		_inference.QueryInferenceAsync(request, ct);

	/// <inheritdoc/>
	public void ClearConversation(string workspace) => _sessions.Clear(workspace);

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
