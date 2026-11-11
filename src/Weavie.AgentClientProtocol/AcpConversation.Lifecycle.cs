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
	internal void Terminate(Exception error) => Terminate(error, cancel: false);

	internal void Terminate(Exception error, bool cancel) {
		lock (_turnTransitionGate) {
			if (Failed || Retired) return;
			lock (_gate) _runtimeFailed = true;
			if (cancel) TerminalizeForRestart(clearSubmissions: true, "Conversation interrupted by a storage failure.");
			TerminalizedTool[] tools;
			bool promptActive;
			lock (_gate) {
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
			if (OwnsWork) Background.End("failed");
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
			_opened.TrySetResult(error);
			_completion.TrySetResult(new("failed", error));
			SignalSettled();
			_retired(this);
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
		if (OwnsWork) Background.End("cancelled");
		RaiseControls();
		foreach (var message in DrainContentStreams()) _pendingTerminalMessages.Enqueue(message);
		foreach (var tool in tools) {
			if (tool.Tool is { NotificationReported: true, Subagent: false }) _pendingTerminalMessages.Enqueue(ToolMessage(tool.Tool));
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

	/// <summary>Ends this incarnation; nothing it does afterwards reaches its owner or the agent.</summary>
	internal AcpConversationHandoff Retire() {
		lock (_turnTransitionGate) {
			_lifetime.Cancel();
			if (OwnsWork) Background.Abandon();
			_opened.TrySetResult(new ObjectDisposedException(nameof(AcpConversation)));
			_completion.TrySetResult(new("closed", null));
			try {
				if (_ownsBinding) _port.Closed(Continuation);
			} finally {
				_port.Detach();
				_retired(this);
				if (_endpoint.IsSet) _endpoint.Value.Retire();
				_terminals.Close();
			}
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
		if (OwnsWork) Background.Abandon();
		ObserveTerminalizedTools(tools);
	}

	/// <summary>Closes the provider session and terminals exactly once, including a session still opening.</summary>
	internal Task CloseAsync() {
		lock (_turnTransitionGate) {
			if (_closing is not null) return _closing;
			var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_closing = completion.Task;
			_ = CloseCoreAsync(completion);
			return _closing;
		}
	}

	private async Task CloseCoreAsync(TaskCompletionSource completion) {
		await Task.Yield();
		try {
			try {
				await RevokeToolsAsync().ConfigureAwait(false);
			} finally {
				await ReleaseAsync().ConfigureAwait(false);
			}
			completion.SetResult();
		} catch (Exception error) {
			completion.SetException(error);
		}
	}

	internal Task RevokeToolsAsync() {
		lock (_turnTransitionGate) return _revocation ??= RevokeCoreAsync();
	}

	private async Task RevokeCoreAsync() {
		await Task.Yield();
		if (_ownsBinding) await _binding.Tools.DisposeAsync().ConfigureAwait(false);
	}

	private async Task ReleaseAsync() {
		bool close;
		AcpSessionEndpoint? endpoint;
		lock (_gate) {
			endpoint = _endpoint.IsSet ? _endpoint.Value : null;
			close = _spec.Role != AcpConversationRole.Subagent && _features.Close && endpoint is not null;
		}
		SettleInteractions();
		var closing = close ? endpoint!.CloseAsync() : Task.CompletedTask;
		endpoint?.Retire();
		try {
			await closing.ConfigureAwait(false);
		} finally {
			await _terminals.DisposeAsync().ConfigureAwait(false);
		}
	}
}
