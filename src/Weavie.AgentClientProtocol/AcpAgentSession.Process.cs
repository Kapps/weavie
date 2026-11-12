using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Start() {
		bool restored;
		lock (_turnTransitionGate) {
			if (_started) {
				return;
			}
			restored = _displayRestored;
			if (!restored && _restoreFailure is null) {
				throw new InvalidOperationException("Restore the saved transcript before starting.");
			}
			_started = true;
		}
		if (!restored) {
			FailProcess(_restoreFailure!); // The runtime stays failed until Restart retries the restore.
		} else {
			try {
				_runtime.Start();
			} catch (Exception error) {
				FailProcess(error);
			}
		}
	}

	/// <inheritdoc/>
	public void Restart() => Restart(clearSubmissions: !Primary.Failed);

	private void Restart(bool clearSubmissions) {
		lock (_turnTransitionGate) {
			if (!_displayRestored) {
				var (messages, blocks) = RestoreDisplay();
				Output.Replace(messages, blocks);
			} else if (_storageFailed) _primary.Save([]);
			_storageFailed = false;
			try {
				_primary.TerminalizeForRestart(clearSubmissions, "ACP agent restarted.");
				SuspendSides("ACP agent restarted.");
				SuspendIndependent("ACP agent restarted.");
				_primary.SettleInteractions();
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error);
				throw;
			}
			// The predecessor dies before the restart fails its pending requests.
			Launch(Succeed(_primary, CreatePrimary), null);
		}
	}

	/// <inheritdoc/>
	public void StartNewConversation() {
		lock (_turnTransitionGate) {
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_clearing) throw new InvalidOperationException("Wait for the conversation to clear.");
			_clearing = true;
		}
		try {
			AcpConversationHandle[] sides;
			lock (_turnTransitionGate) {
				var predecessor = _primary;
				sides = Sides();
				// Decided before settling: settling ends the background work only a restart actually stops.
				var process = ReplaceableProcess(predecessor);
				try {
					TerminalizeConversations("Conversation interrupted by /clear.");
					predecessor.SettleInteractions();
					_sessions.Replace(_definition.Id, _context.Workspace, [], []);
				} catch (AcpSessionStoreException error) {
					StopForStorageFailure(error);
					throw;
				}
				var successor = Succeed(predecessor, handoff => CreatePrimary(handoff with {
					Continuation = NewContinuation(string.Empty, 0, string.Empty, guidanceSent: false),
				}));
				_sides.Clear();
				foreach (var side in sides) side.Conversation.Retire();
				_sideConversations.Clear();
				_storageFailed = false;
				_displayRestored = true;
				successor.Publish(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
				Launch(successor, process);
			}
			foreach (var side in sides) DisposeSide(side);
		} finally {
			lock (_turnTransitionGate) _clearing = false;
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		lock (_turnTransitionGate) {
			if (_disposed) return;
			_disposed = true;
			_sides.Clear();
		}
		await _runtime.DisposeAsync().ConfigureAwait(false);
		await _archiveStop.ConfigureAwait(false);
	}

	private bool FailProcess(Exception error) => _runtime.Fail(error);

	private void StopForStorageFailure(AcpSessionStoreException error) {
		ArchiveFailed(error);
		_runtime.Fail(error, cancel: true);
	}

	private void TerminalizeConversations(string summary) {
		foreach (var conversation in Roots()) conversation.TerminalizeForRestart(clearSubmissions: true, summary);
		foreach (var conversation in _runtime.Conversations.Where(conversation => conversation.Independent)) _ = _runtime.CloseAsync(conversation);
	}
}
