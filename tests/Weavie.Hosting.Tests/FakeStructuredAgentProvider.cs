using System.Collections.Concurrent;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Tests;

internal sealed class FakeStructuredAgentProvider : IAgentProvider {
	// The provider owns its conversation the way a real agent owns its on-disk transcript: it outlives a worker
	// restart and is replayed on load. Keyed by worktree so each session replays only its own.
	private static readonly ConcurrentDictionary<string, List<AgentPaneMessage>> Transcripts =
		new(StringComparer.Ordinal);

	/// <summary>Drops every provider-owned transcript, modelling an agent that cannot replay.</summary>
	public static void ForgetTranscripts() => Transcripts.Clear();
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

	public IAgentSession CreateSession(AgentSessionContext context) {
		ArgumentNullException.ThrowIfNull(context);
		return new FakeStructuredAgentSession(
			context.Events,
			Transcripts.GetOrAdd(context.Workspace, static _ => []));
	}

	// Emits a deterministic, persistable turn (a user echo + a completed agent message) so the transcript store
	// has real content to persist and replay. Keeps everything synchronous for race-free tests.
	private sealed class FakeStructuredAgentSession(IAgentEventSink events, List<AgentPaneMessage> transcript)
		: IStructuredAgentSession, IStructuredAgentControls {
		private bool _started;
		private int _turns;

		public event Action<AgentPaneMessage>? PaneMessage;
		public event Action<IReadOnlyList<AgentPaneMessage>>? PaneSnapshot;
		public event Action<AgentControlState>? ControlStateChanged;

		// Every replayable record the provider emits, so a reload can hand back the same conversation.
		private void Emit(AgentPaneMessage message) {
			transcript.Add(message);
			PaneMessage?.Invoke(message);
		}

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
			if (transcript.Count > 0) {
				_turns = transcript.Count(message => message.Type == "user-message");
				PaneSnapshot?.Invoke([.. transcript]);
			}

			ControlStateChanged?.Invoke(ControlState);
		}

		public void Submit(AgentTurnSubmission submission) {
			ArgumentNullException.ThrowIfNull(submission);
			if (!_started) {
				return;
			}

			if (submission.Text == ResetPrompt) {
				transcript.Clear();
				PaneMessage?.Invoke(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
				return;
			}
			if (submission.Text == PlanPrompt) {
				_turns++;
				string turn = $"turn-{_turns}";
				string planItem = $"plan-{_turns}";
				Emit(new AgentPaneMessage {
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
				Emit(new AgentPaneMessage {
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
			Emit(new AgentPaneMessage {
				Type = "user-message",
				ProviderId = "structured",
				ThreadId = "thread-fake",
				TurnId = $"turn-{_turns}",
				Text = submission.Text,
			});
			Emit(new AgentPaneMessage {
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
