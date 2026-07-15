using Weavie.Core.Agents;

namespace Weavie.Hosting.Tests;

internal sealed class FakeCodexAgentProvider : IAgentProvider {
	/// <summary>A prompt that makes the fake abandon its thread (emit a <c>transcript-reset</c>) instead of answering.</summary>
	public const string ResetPrompt = "reset-thread";

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

	// Emits a deterministic, persistable turn (a user echo + a completed agent message) so the transcript store
	// has real content to persist and replay. Keeps everything synchronous for race-free tests.
	private sealed class FakeCodexAgentSession : IStructuredAgentSession, IStructuredAgentControls {
		private bool _started;
		private int _turns;

		public event Action<AgentPaneMessage>? PaneMessage;
		public event Action<AgentControlState>? ControlStateChanged;

		public AgentControlState ControlState { get; } = new() {
			ModelControl = new AgentModelControl {
				Value = "gpt-test",
				ValueLabel = "GPT Test",
				Models = [new AgentModelChoice {
					Id = "gpt-test",
					Label = "GPT Test",
					Current = true,
					Effort = string.Empty,
					Efforts = [],
					FastTier = string.Empty,
					FastOn = false,
				}],
			},
			Axes = [
				Axis("approvalPolicy", "Approvals", "On request"),
				Axis("sandbox", "Sandbox", "Workspace write"),
			],
			Slash = [],
		};

		public void Start() {
			if (_started) {
				return;
			}
			_started = true;
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "thread-ready", ProviderId = "codex", Status = "ready" });
			ControlStateChanged?.Invoke(ControlState);
		}

		public void Submit(AgentTurnSubmission submission) {
			ArgumentNullException.ThrowIfNull(submission);
			if (submission.Text == ResetPrompt) {
				PaneMessage?.Invoke(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
				return;
			}

			_turns++;
			string item = $"item-{_turns}";
			PaneMessage?.Invoke(new AgentPaneMessage {
				Type = "user-message",
				ProviderId = "codex",
				ThreadId = "thread-fake",
				TurnId = $"turn-{_turns}",
				Text = submission.Text,
			});
			PaneMessage?.Invoke(new AgentPaneMessage {
				Type = "item-completed",
				ProviderId = "codex",
				ThreadId = "thread-fake",
				TurnId = $"turn-{_turns}",
				ItemId = item,
				ItemType = "agentMessage",
				Text = $"echo: {submission.Text}",
				Status = "completed",
			});
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

		public void SetControl(string axis, string value) {
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;

		private static AgentControlAxis Axis(string id, string label, string valueLabel) => new() {
			Id = id,
			Label = label,
			Value = valueLabel,
			ValueLabel = valueLabel,
			Options = [],
		};
	}
}
