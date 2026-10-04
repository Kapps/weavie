using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	internal void OnProcessStarted(long generation) {
		lock (_turnTransitionGate) {
			lock (_gate) {
				_activeGeneration = generation;
				_endpoint = null;
				_terminals = new AcpTerminalManager(_context.Workspace, _log);
				// An untouched primary has no conversation to resume; a side fork can have inherited history.
				if (!_spec.SideScoped && _turnNumber == 0) _sessionId = null;
				_ready = false;
				_promptActive = false;
				_steering = false;
				_waitingForBackground = false;
				_cancelRequested = false;
				_controlMutations.Clear();
				_controlMutationActive = false;
				_runtimeFailed = false;
				_sessionOpening = false;
				_loadingTranscript = false;
				_controls.Clear();
				_configOwnsMode = false;
				_commands = [];
				_tools.Clear();
				_activeTools.Clear();
				_content.Clear();
				_turnItemIds.Clear();
				_contextUsage = null;
				_usageLimits.Clear();
			}
		}
		CancelPendingInteractions();
		AbandonClientRequests();
		RaiseControls();
		_port.UsageChanged(Usage);
	}

	/// <summary>Ends this conversation after an unrecoverable failure.</summary>
	internal void Terminate(Exception error) {
		lock (_turnTransitionGate) {
			TerminalizedTool[] tools;
			bool promptActive;
			lock (_gate) {
				if (_disposed || _runtimeFailed) return;
				_activeGeneration = 0;
				_runtimeFailed = true;
				_ready = false;
				promptActive = _promptActive;
				_promptActive = false;
				_steering = false;
				_waitingForBackground = false;
				_cancelRequested = false;
				_controlMutations.Clear();
				_submissionEpoch++;
				tools = TerminalizeActiveToolsLocked("failed");
			}
			_terminals.Close();
			AbandonClientRequests();
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
			SignalSideTurnSettled();
		}
	}

	/// <summary>Stops live work for a restart, settling everything it published.</summary>
	internal void TerminalizeForRestart(bool clearSubmissions, string summary) {
		TerminalizedTool[] tools;
		bool promptActive;
		long generation;
		lock (_gate) {
			generation = _activeGeneration;
			_activeGeneration = 0;
			_ready = false;
			if (clearSubmissions) _pendingSubmissions.Clear();
			_submissionEpoch++;
			_cancelRequested = false;
			promptActive = _promptActive;
			_promptActive = false;
			_steering = false;
			_waitingForBackground = false;
			tools = TerminalizeActiveToolsLocked("cancelled");
		}
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
		if (generation > 0) _terminals.Close();
		PublishQueue();
		ObserveTerminalizedTools(tools);
		if (promptActive || tools.Length > 0) Observe(new AgentTurnStopped(WillResume: false));
		while (_pendingTerminalMessages.TryPeek(out var message)) {
			Emit(message);
			_pendingTerminalMessages.Dequeue();
		}
	}

	internal void MarkFailed() {
		lock (_gate) _runtimeFailed = true;
	}

	internal void ReportFailure(Exception error) {
		Observe(new AgentRuntimeFailed());
		EmitFailure(error);
	}

	/// <summary>Detaches the conversation from its owner; nothing it does afterwards reaches the owner.</summary>
	internal void Retire() {
		_port.Detach();
		lock (_gate) _endpoint?.Retire();
	}

	internal void SettleForDisposal() {
		TerminalizedTool[] tools;
		lock (_gate) {
			_disposed = true;
			_controlMutations.Clear();
			tools = TerminalizeActiveToolsLocked("cancelled");
		}
		ObserveTerminalizedTools(tools);
	}

	/// <summary>Closes the provider session around <paramref name="teardown"/>, then releases the terminals.</summary>
	internal async Task CloseAsync(Func<Task> teardown) {
		bool close;
		AcpSessionEndpoint? endpoint;
		lock (_turnTransitionGate) SettleForDisposal();
		lock (_gate) {
			endpoint = _endpoint;
			close = (_ready || _spec.SideScoped) && _features.Close && endpoint?.SessionId is not null;
		}
		CancelPendingInteractions();
		AbandonClientRequests();
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
