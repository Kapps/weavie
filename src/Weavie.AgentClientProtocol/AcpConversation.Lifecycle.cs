using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	/// <summary>Binds this conversation to a started process; a conversation attaches once.</summary>
	internal void Attach(AcpProcess process) {
		lock (_turnTransitionGate) _endpoint.Set(process.OpenEndpoint(HandleNotification, RegisterClientRequest, FailRuntime));
		Announce();
	}

	/// <summary>Publishes this conversation's controls and usage to its owner.</summary>
	internal void Announce() {
		RaiseControls();
		_port.UsageChanged(Usage);
	}

	/// <summary>Takes over a retired predecessor's queue and the interaction identities already shown in its pane.</summary>
	internal void Inherit(AcpConversationHandoff predecessor) {
		lock (_gate) {
			foreach (var submission in predecessor.Pending) _pendingSubmissions.Enqueue(submission);
			_resolvedRequests.UnionWith(predecessor.ResolvedRequests);
			_authenticationSequence = Math.Max(_authenticationSequence, predecessor.AuthenticationSequence);
		}
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
		if (promptActive) CancelPrompt();
		AbandonRunningRequests();
		// Abandoned requests complete before the lifetime cancels their linked tokens.
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
	internal AcpConversationHandoff Retire() => Retire(static endpoint => endpoint.Retire());

	/// <summary>Ends this incarnation on a process that keeps running, closing its provider session there.</summary>
	internal AcpConversationHandoff RetireClosing() => Retire(static endpoint => _ = endpoint.CloseAsync());

	private AcpConversationHandoff Retire(Action<AcpSessionEndpoint> release) {
		lock (_turnTransitionGate) {
			_lifetime.Cancel();
			_port.Detach();
			if (_endpoint.IsSet) release(_endpoint.Value);
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

	/// <summary>Closes the provider session of a settled or ended conversation around <paramref name="teardown"/>.</summary>
	internal async Task CloseAsync(Func<Task> teardown) {
		bool close;
		AcpSessionEndpoint? endpoint;
		lock (_gate) {
			endpoint = _endpoint.IsSet ? _endpoint.Value : null;
			close = (_ready || _spec.Side) && _features.Close;
		}
		SettleInteractions();
		var closing = close ? endpoint!.CloseAsync() : Task.CompletedTask;
		endpoint?.Retire();
		try {
			await teardown().ConfigureAwait(false);
		} finally {
			await closing.ConfigureAwait(false);
			await _terminals.DisposeAsync().ConfigureAwait(false);
		}
	}
}
