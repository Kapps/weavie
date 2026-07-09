using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Hooks;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;

namespace Weavie.Hosting.Agents.Claude;

/// <summary>A complete Claude provider session: CLI lifecycle, IDE integration, hooks, and capability registry.</summary>
public sealed class ClaudeAgentSession : ITerminalAgentSession {
	private readonly IAgentEventSink _events;
	private readonly ClaudeTerminalLifecycle _terminal;

	/// <summary>Creates and starts one worktree-scoped Claude integration.</summary>
	public ClaudeAgentSession(
		SettingsStore settings,
		string workspace,
		ClaudeSessionStore sessions,
		IClaudeTranscripts transcripts,
		CapabilityRegistryHost registry,
		IDiffPresenter presenter,
		EditorStore editor,
		HostRuntimeInfo runtime,
		IAgentEventSink events) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentException.ThrowIfNullOrEmpty(workspace);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(transcripts);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(presenter);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(runtime);
		ArgumentNullException.ThrowIfNull(events);
		_events = events;
		Registry = registry;
		Ide = new ClaudeIdeIntegration(
			registry, presenter, [workspace], "weavie", settings, editor, runtime, ObserveHook, ObserveDecision);
		Ide.Server.Log += Tagged("[mcp]");
		Ide.HookBridge.Observed += request => {
			Console.WriteLine($"[hook] {request.Event} {request.ToolName}");
			Console.Out.Flush();
		};
		Ide.HookBridge.Log += Tagged("[hook]");
		registry.Server.Log += Tagged("[registry]");
		_terminal = new ClaudeTerminalLifecycle(
			settings,
			workspace,
			sessions,
			transcripts,
			new ClaudeLaunchConfiguration {
				Environment = Ide.EnvironmentVariables,
				McpConfigPath = Ide.WriteMcpConfigFile(),
				SettingsFilePath = Ide.WriteSettingsFile(),
				SystemPromptFilePath = Ide.WriteSystemPromptFile(),
			});
		Console.WriteLine(
			$"[weavie] IDE-MCP on 127.0.0.1:{Ide.Port}; registry on 127.0.0.1:{registry.Port}; "
			+ $"workspace {workspace}; lock {Ide.LockFilePath}");
		Console.Out.Flush();
	}

	/// <summary>Claude's provider-specific IDE and hook integration.</summary>
	public ClaudeIdeIntegration Ide { get; }

	/// <summary>The standard capability registry shared with the provider.</summary>
	public CapabilityRegistryHost Registry { get; }

	/// <inheritdoc/>
	public AgentLaunch ResolveLaunch() => _terminal.ResolveLaunch();

	/// <inheritdoc/>
	public void ObserveTerminalOutput(ReadOnlyMemory<byte> data) => _terminal.ObserveTerminalOutput(data);

	/// <inheritdoc/>
	public void ObserveTerminalInput(ReadOnlyMemory<byte> data) => _terminal.ObserveTerminalInput(data);

	/// <inheritdoc/>
	public void ObserveProcessExit(AgentProcessExit exit) => _terminal.ObserveProcessExit(exit);

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		await Ide.DisposeAsync().ConfigureAwait(false);
		await Registry.DisposeAsync().ConfigureAwait(false);
	}

	private AgentEventFeedback ObserveHook(HookRequest request) {
		try {
			_terminal.ObserveHook(request);
			var messages = new List<string>();
			foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
				messages.AddRange(_events.Observe(value).Messages);
			}
			return messages.Count == 0 ? AgentEventFeedback.None : new AgentEventFeedback { Messages = messages };
		} catch (Exception ex) {
			Console.WriteLine($"[hook] hook observer threw: {ex.Message}");
			Console.Out.Flush();
			return AgentEventFeedback.None;
		}
	}

	private void ObserveDecision(HookRequest request, HookDecision decision) {
		if (request.Event == HookEventKind.PermissionRequest) {
			_events.Observe(new AgentPermissionResolved(decision.Kind == HookDecisionKind.PassThrough));
		}
	}

	private static Action<string> Tagged(string tag) => line => {
		Console.WriteLine($"{tag} {line}");
		Console.Out.Flush();
	};
}
