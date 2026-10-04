using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	/// <summary>Binds this conversation to a started process; a conversation attaches once.</summary>
	internal void Attach(AcpProcess process) {
		lock (_turnTransitionGate) _endpoint.Set(process.OpenEndpoint(HandleNotification, RegisterClientRequest, FailRuntime));
		RaiseControls();
		_port.UsageChanged(Usage);
	}

	/// <summary>Ends this conversation after an unrecoverable failure.</summary>
	internal void Terminate(Exception error) {
		lock (_turnTransitionGate) {
			TerminalizedTool[] tools;
			bool promptActive;
			lock (_gate) {
				if (!Live) return;
				_runtimeFailed = true;
				_ready = false;
				promptActive = _promptActive;
				_promptActive = false;
				_steering = false;
				_waitingForBackground = false;
				_cancelRequested = false;
				_controlMutations.Clear();
				tools = TerminalizeActiveToolsLocked("failed");
			}
			_terminals.Close();
			AbandonClientRequests();
			// Abandoned requests complete before the lifetime cancels their linked tokens.
			_lifetime.Cancel();
			ObserveTerminalizedTools(tools);
			RaiseControls();
			Observe(new AgentRuntimeFailed());
			CompleteContentStreams();
			PublishTerminalizedToolMessages(tools);
			if (promptActive) {
				Emit(new AgentPaneMessage {
					Type = "turn-completed",
					ProviderId = Definition.Id,
					ThreadId = SessionId(),
					TurnId = TurnId(),
					Status = "failed",
					Summary = error.Message,
				});
			}
			EmitFailure(error);
			SignalSettled();
		}
	}

	/// <summary>Stops live work for a restart, settling everything it published.</summary>
	internal void TerminalizeForRestart(bool clearSubmissions, string summary) {
		TerminalizedTool[] tools;
		bool promptActive;
		lock (_gate) {
			_ready = false;
			if (clearSubmissions) _pendingSubmissions.Clear();
			_cancelRequested = false;
			promptActive = _promptActive;
			_promptActive = false;
			_steering = false;
			_waitingForBackground = false;
			tools = TerminalizeActiveToolsLocked("cancelled");
		}
		_lifetime.Cancel();
		RaiseControls();
		foreach (var message in DrainContentStreams()) _pendingTerminalMessages.Enqueue(message);
		foreach (var tool in tools) {
			if (tool.Tool.NotificationReported) _pendingTerminalMessages.Enqueue(ToolMessage(tool.Tool));
		}
		if (promptActive) {
			_pendingTerminalMessages.Enqueue(new AgentPaneMessage {
				Type = "turn-completed",
				ProviderId = Definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				Status = "cancelled",
				Summary = summary,
			});
		}
		_terminals.Close();
		PublishQueue();
		ObserveTerminalizedTools(tools);
		if (promptActive || tools.Length > 0) Observe(new AgentTurnStopped(WillResume: false));
		while (_pendingTerminalMessages.TryPeek(out var message)) {
			Emit(message);
			_pendingTerminalMessages.Dequeue();
		}
	}

	/// <summary>Cancels every request still waiting on the user or this client.</summary>
	internal void SettleInteractions() {
		CancelPendingInteractions();
		AbandonClientRequests();
	}

	internal void MarkFailed() {
		lock (_gate) _runtimeFailed = true;
	}

	internal void ReportFailure(Exception error) {
		Observe(new AgentRuntimeFailed());
		EmitFailure(error);
	}

	/// <summary>Ends this incarnation; nothing it does afterwards reaches its owner or the agent.</summary>
	internal AcpConversationHandoff Retire() {
		lock (_turnTransitionGate) {
			_lifetime.Cancel();
			_port.Detach();
			if (_endpoint.IsSet) _endpoint.Value.Retire();
			_terminals.Close();
			lock (_gate) return new(Continuation, _pendingSubmissions.Snapshot(), [.. _resolvedRequests], _authenticationSequence);
		}
	}

	internal void SettleForDisposal() {
		TerminalizedTool[] tools;
		lock (_gate) {
			_disposed = true;
			_controlMutations.Clear();
			tools = TerminalizeActiveToolsLocked("cancelled");
		}
		_lifetime.Cancel();
		ObserveTerminalizedTools(tools);
	}

	/// <summary>Closes the provider session around <paramref name="teardown"/>, then releases the terminals.</summary>
	internal async Task CloseAsync(Func<Task> teardown) {
		bool close;
		AcpSessionEndpoint? endpoint;
		lock (_turnTransitionGate) SettleForDisposal();
		lock (_gate) {
			endpoint = _endpoint.IsSet ? _endpoint.Value : null;
			close = (_ready || _spec.SideScoped) && _features.Close && endpoint?.SessionId is not null;
		}
		SettleInteractions();
		var closeRequest = close ? endpoint!.CloseAsync() : null;
		endpoint?.Retire();
		try {
			await teardown().ConfigureAwait(false);
		} finally {
			if (closeRequest is not null) {
				try {
					await closeRequest.ConfigureAwait(false);
				} catch (Exception ex) {
					_log($"[acp:{Definition.Id}] session/close ended during process teardown: {ex.Message}");
				}
			}
			await _terminals.DisposeAsync().ConfigureAwait(false);
		}
	}
}
