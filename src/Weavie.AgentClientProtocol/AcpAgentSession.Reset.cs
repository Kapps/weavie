using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private readonly Queue<AgentPaneMessage> _pendingTerminalMessages = new();

	/// <inheritdoc/>
	public void Restart() {
		bool clearSubmissions;
		lock (_gate) clearSubmissions = !_runtimeFailed;
		Restart(clearSubmissions);
	}

	private void Restart(bool clearSubmissions) {
		lock (_turnTransitionGate) {
			if (_role is SideRole) throw new InvalidOperationException("Restart the owning primary conversation.");
			if (!_displayRestored) RestoreDisplay();
			else if (_storageFailed) SaveContinuation();
			_storageFailed = false;
			long generation;
			SideRuntime[] sides;
			lock (_gate) {
				generation = _activeGeneration;
				sides = [.. _sideRuntimes.Values];
			}
			try {
				TerminalizeForRestart(clearSubmissions, "ACP agent restarted.");
				SuspendSideRuntimes("ACP agent restarted.");
				_connection.Restart();
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, generation, sides);
				throw;
			}
		}
	}

	/// <inheritdoc/>
	public void StartNewConversation() {
		if (_role is not PrimaryRole) {
			throw new InvalidOperationException("Only the primary ACP conversation can be replaced.");
		}
		SideRuntime[] sideSessions;
		lock (_turnTransitionGate) {
			long generation;
			lock (_gate) {
				generation = _activeGeneration;
				sideSessions = [.. _sideRuntimes.Values];
			}
			try {
				TerminalizeConversations("Conversation interrupted by /clear.", sideSessions);
				_sessions.Clear(_definition.Id, _context.Workspace);
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, generation, sideSessions);
				throw;
			}
			lock (_gate) {
				_sessionId = null;
				_turnNumber = 0;
				_guidanceSent = false;
				_planTurns.Clear();
				_sideRuntimes.Clear();
			}
			_sideConversations.Clear();
			_storageFailed = false;
			_displayRestored = true;
			Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
			_connection.Restart();
		}
		foreach (var side in sideSessions) DisposeSideRuntime(side);
	}

	private void StopForStorageFailure(AcpSessionStoreException error, long generation, IReadOnlyList<SideRuntime> sides) {
		_storageFailed = true;
		lock (_gate) _runtimeFailed = true;
		if (generation > 0) _connection.TerminateGeneration(generation, error.Message);
		TerminalizeConversations("Conversation interrupted by a storage failure.", sides);
		Observe(new AgentRuntimeFailed());
		EmitFailure(error);
	}

	private void TerminalizeConversations(string summary, IReadOnlyList<SideRuntime> sides) {
		TerminalizeForRestart(clearSubmissions: true, summary);
		foreach (var side in sides) side.Session.TerminalizeForRestart(clearSubmissions: true, summary);
	}

	private void TerminalizeForRestart(bool clearSubmissions, string summary) {
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
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				Status = "cancelled",
				Summary = summary,
			});
		}
		if (generation > 0) _terminals.ReleaseGeneration(generation);
		PublishQueue();
		ObserveTerminalizedTools(tools);
		if (promptActive || tools.Length > 0) Observe(new AgentTurnStopped(WillResume: false));
		while (_pendingTerminalMessages.TryPeek(out var message)) {
			Emit(message);
			_pendingTerminalMessages.Dequeue();
		}
	}
}
