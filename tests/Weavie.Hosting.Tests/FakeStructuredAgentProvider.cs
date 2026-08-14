using Weavie.Core.Agents;

namespace Weavie.Hosting.Tests;

internal sealed class FakeStructuredAgentProvider : IAgentProvider {
	/// <summary>A prompt that makes the fake abandon its thread (emit a <c>transcript-reset</c>) instead of answering.</summary>
	public const string ResetPrompt = "reset-thread";

	/// <summary>A prompt that makes the fake emit one completed Markdown plan.</summary>
	public const string PlanPrompt = "emit-plan";

	public const string PlanMarkdown = "# Plan\n\n- Inspect the code\n- Implement the fix";

	public AgentProviderInfo Info { get; } = new() {
		Id = "structured",
		Name = "Fake structured agent",
		Capabilities = AgentProviderCapabilities.StructuredPane
			| AgentProviderCapabilities.CapabilityRegistry
			| AgentProviderCapabilities.Ide
			| AgentProviderCapabilities.Events,
		Available = true,
	};

	public IAgentSession CreateSession(AgentSessionContext context) => new FakeStructuredAgentSession(context.Events);

	// Emits a deterministic, persistable turn (a user echo + a completed agent message) so the transcript store
	// has real content to persist and replay. Keeps everything synchronous for race-free tests.
	private sealed class FakeStructuredAgentSession(IAgentEventSink events) : IStructuredAgentSession, IStructuredAgentControls {
		private bool _started;
		private int _turns;

		public event Action<AgentPaneMessage>? PaneMessage;
		public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot { add { } remove { } }
		public event Action<AgentControlState>? ControlStateChanged;

		public AgentControlState ControlState { get; } = new() {
			Axes = [
				Axis("model", "Model", "GPT Test"),
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
			events.Observe(new AgentSessionStarted("startup"));
			PaneMessage?.Invoke(new AgentPaneMessage { Type = "thread-ready", ProviderId = "structured", Status = "ready" });
			ControlStateChanged?.Invoke(ControlState);
		}

		public void Submit(AgentTurnSubmission submission) {
			ArgumentNullException.ThrowIfNull(submission);
			if (!_started) {
				return;
			}

			if (submission.Text == ResetPrompt) {
				PaneMessage?.Invoke(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
				return;
			}
			if (submission.Text == PlanPrompt) {
				_turns++;
				string turn = $"turn-{_turns}";
				string planItem = $"plan-{_turns}";
				PaneMessage?.Invoke(new AgentPaneMessage {
					Type = "plan-delta",
					ProviderId = "structured",
					ThreadId = "thread-fake",
					TurnId = turn,
					ItemId = planItem,
					ItemType = "plan",
					Category = "plan",
					Text = "# Plan",
					Status = "inProgress",
				});
				PaneMessage?.Invoke(new AgentPaneMessage {
					Type = "item-completed",
					ProviderId = "structured",
					ThreadId = "thread-fake",
					TurnId = turn,
					ItemId = planItem,
					ItemType = "plan",
					Category = "plan",
					Text = PlanMarkdown,
					Status = "completed",
				});
				return;
			}

			_turns++;
			string item = $"item-{_turns}";
			PaneMessage?.Invoke(new AgentPaneMessage {
				Type = "user-message",
				ProviderId = "structured",
				ThreadId = "thread-fake",
				TurnId = $"turn-{_turns}",
				Text = submission.Text,
			});
			PaneMessage?.Invoke(new AgentPaneMessage {
				Type = "item-completed",
				ProviderId = "structured",
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

		public void ResolvePermission(string requestId, string optionId) {
		}

		public void ResolveInput(
			string requestId,
			string action,
			IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		}

		public void Authenticate(string methodId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		}

		public void SetControl(string axis, string value) {
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;

		private static AgentControlAxis Axis(string id, string label, string valueLabel) => new() {
			Id = id,
			Label = label,
			Kind = "select",
			Value = valueLabel,
			ValueLabel = valueLabel,
			Options = [],
		};
	}
}
