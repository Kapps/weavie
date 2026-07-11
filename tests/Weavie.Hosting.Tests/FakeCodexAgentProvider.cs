using Weavie.Core.Agents;

namespace Weavie.Hosting.Tests;

internal sealed class FakeCodexAgentProvider : IAgentProvider {
	public AgentProviderInfo Info { get; } = new() {
		Id = "codex",
		Name = "Codex (WIP)",
		Capabilities = AgentProviderCapabilities.StructuredPane
			| AgentProviderCapabilities.CapabilityRegistry
			| AgentProviderCapabilities.Ide
			| AgentProviderCapabilities.Events,
		Available = true,
	};

	public IAgentSession CreateSession(AgentSessionContext context) => new FakeCodexAgentSession();

	private sealed class FakeCodexAgentSession : IStructuredAgentSession {
		public event Action<AgentPaneMessage>? PaneMessage;

		public void Start() =>
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "thread-ready", ProviderId = "codex", Status = "ready" });

		public void SubmitPrompt(string prompt) => SubmitPrompt(prompt, new AgentTurnOptions(null, null, false));
		public void SubmitPrompt(string prompt, AgentTurnOptions options) {
		}

		public void AttachImage(string path) {
		}

		public void PrefillPrompt(string prompt) {
		}

		public void Interrupt() {
		}

		public void Restart() {
		}

		public void ResolveApproval(string requestId, string decision) {
		}

		public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
