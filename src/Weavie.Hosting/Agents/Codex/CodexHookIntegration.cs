using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Claude;
using Weavie.Core.Hooks;

namespace Weavie.Hosting.Agents.Codex;

internal sealed class CodexHookIntegration : ICodexHookIntegration {
	private readonly HookBridgeServer _bridge;

	public CodexHookIntegration(int registryPort, IAgentEventSink events, Action<string> log) {
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(log);
		string relay = HookRelayBinary.PathIn(AppContext.BaseDirectory);
		HookRelayBinary.RequireExists(relay);
		string pipe = HookProtocol.PipeName(registryPort);
		_bridge = new HookBridgeServer(pipe, decide: null);
		_bridge.Observed += request => Observe(events, request, log);
		_bridge.Log += log;
		_bridge.Start();
		GlobalArguments = ["--dangerously-bypass-hook-trust"];
		AppServerArguments = BuildArguments(relay);
		Environment = new Dictionary<string, string>(StringComparer.Ordinal) {
			[HookProtocol.PipeEnvVar] = pipe,
		};
	}

	public IReadOnlyList<string> GlobalArguments { get; }

	public IReadOnlyList<string> AppServerArguments { get; }

	public IReadOnlyDictionary<string, string> Environment { get; }

	public ValueTask DisposeAsync() => _bridge.DisposeAsync();

	private static void Observe(IAgentEventSink events, HookRequest request, Action<string> log) {
		try {
			foreach (var value in ClaudeHookEventAdapter.Adapt(request)) {
				events.Observe(value);
			}
		} catch (Exception ex) {
			log($"[codex-hook] observer failed: {ex.Message}");
		}
	}

	private static IReadOnlyList<string> BuildArguments(string relay) {
		string value = HookValue(relay);
		return [
			"-c", "hooks.PreToolUse=" + value,
			"-c", "hooks.PostToolUse=" + value,
		];
	}

	private static string HookValue(string relay) {
		string command = JsonSerializer.Serialize($"\"{relay}\"");
		return "[{matcher=\"*\",hooks=[{type=\"command\",command=" + command + ",timeout=30}]}]";
	}
}
