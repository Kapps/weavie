using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	/// <summary>The background work of this conversation's root; a subagent shares its root's.</summary>
	internal AcpBackgroundWork Background { get; }

	internal string ConversationId => _spec.Seed.Continuation.ConversationId;

	private bool OwnsWork => _spec.Role != AcpConversationRole.Subagent;

	internal AcpConversation CreateSubagent(AcpConversationSpec spec) => _port.CreateSubagent(spec);

	internal void RaiseBackground() => _port.BackgroundChanged();

	/// <summary>Takes over the announced subagent session on its parent's process and starts its single read-only turn.</summary>
	internal void Adopt(AcpConversation announcer, string name, long startedAtMs) {
		var opening = (AdoptedOpening)_spec.Opening;
		_endpoint.Set(announcer._endpoint.Value.OpenChild(opening.SessionId, HandleNotification, RegisterClientRequest, FailRuntime));
		lock (_gate) {
			_ready = true;
			_promptActive = true;
			_turnNumber = 1;
		}
		Publish(new AgentPaneMessage {
			Type = "subagent-started",
			ProviderId = Definition.Id,
			ThreadId = opening.SessionId,
			ParentItemId = opening.ParentConversationId,
			Summary = name,
			Text = _spec.Seed.Continuation.InitialPrompt,
			StartedAtMs = startedAtMs,
			Status = "running",
		});
		Emit(new AgentPaneMessage {
			Type = "turn-started",
			ProviderId = Definition.Id,
			ThreadId = opening.SessionId,
			TurnId = TurnId(),
			IsPrimaryThread = false,
			StartedAtMs = startedAtMs,
		});
	}

	/// <summary>Ends a subagent's turn with the provider's <paramref name="state"/>, settling everything it published.</summary>
	internal void EndSubagent(string state) {
		lock (_turnTransitionGate) {
			TerminalizedTool[] tools;
			bool running;
			lock (_gate) {
				running = _promptActive;
				_promptActive = false;
				_ready = false;
				tools = TerminalizeActiveToolsLocked(state == "failed" ? "failed" : "cancelled");
			}
			if (!running || !Live) return;
			SettleInteractions();
			_terminals.Close();
			ObserveTerminalizedTools(tools);
			CompleteContentStreams();
			PublishTerminalizedToolMessages(tools);
			Emit(new AgentPaneMessage {
				Type = "turn-completed",
				ProviderId = Definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				Status = state,
			});
		}
	}

	private void HandleBackgroundUpdate(string kind, JsonElement update) {
		switch (kind) {
			case "subagent_spawned": Background.Spawn(this, update); break;
			case "subagent_state_update": Background.Finish(update); break;
			case "async_task_spawned": Background.SpawnTask(this, update); break;
			case "async_task_progress": Background.ProgressTask(update); break;
			default: Background.UpdateTaskState(update); break;
		}
	}

	/// <summary>Journals a background task's transcript card at the turn it started in.</summary>
	internal void PublishTaskCard(string turnId, AgentBackgroundItem item) => Emit(new AgentPaneMessage {
		Type = item.Running ? "item-started" : "item-completed",
		ProviderId = Definition.Id,
		ThreadId = SessionId(),
		TurnId = turnId,
		ItemId = item.TranscriptItemId,
		ItemType = "backgroundTask",
		Category = item.Type,
		Summary = item.Name,
		Text = item.Detail,
		Status = item.State.ToString().ToLowerInvariant(),
		StartedAtMs = item.StartedAtMs,
		CompletedAtMs = item.EndedAtMs,
	});

	/// <summary>Asks the agent to stop one of this root's tasks; the stop always addresses the root session.</summary>
	internal async Task<bool> StopTaskAsync(string asyncTaskId) {
		Task<JsonElement> request;
		lock (_turnTransitionGate) {
			if (!Live) throw new InvalidOperationException("The conversation that owns this task has ended.");
			request = _endpoint.Value.RequestAsync(
				"_session/async_task/stop", new System.Text.Json.Nodes.JsonObject { ["asyncTaskId"] = asyncTaskId }, CancellationToken.None);
		}
		var result = await request.ConfigureAwait(false);
		return result.TryGetProperty("stopped", out var stopped) && stopped.ValueKind is JsonValueKind.True or JsonValueKind.False
			? stopped.GetBoolean()
			: throw new AcpProtocolException("The async task stop response is missing 'stopped'.");
	}

	private void ObserveReplayedBackground(string kind, JsonElement update) {
		if (kind != "subagent_spawned") return;
		string subagent = RequiredString(update, "subagentSessionId", "subagent_spawned update");
		_endpoint.Value.Sink(subagent);
		Background.Replayed(subagent);
	}
}
