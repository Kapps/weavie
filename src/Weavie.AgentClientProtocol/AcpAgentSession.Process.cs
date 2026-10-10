using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Start() {
		bool restored;
		lock (_gate) {
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
				_connection.Start();
			} catch (Exception error) {
				FailProcess(error);
			}
		}
	}

	/// <inheritdoc/>
	public void Restart() => Restart(clearSubmissions: !Primary.Failed);

	private void Restart(bool clearSubmissions) {
		lock (_turnTransitionGate) {
			if (!_displayRestored) PaneSnapshot?.Invoke(RestoreDisplay());
			else if (_storageFailed) _primary.Save([]);
			_storageFailed = false;
			var sides = Sides();
			try {
				_primary.TerminalizeForRestart(clearSubmissions, "ACP agent restarted.");
				SuspendSides("ACP agent restarted.");
				_primary.SettleInteractions();
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, sides);
				throw;
			}
			// The predecessor dies before the restart fails its pending requests.
			Launch(Succeed(_primary, null, CreatePrimary), null);
		}
	}

	/// <inheritdoc/>
	public void StartNewConversation() {
		SideRuntime[] sides;
		lock (_turnTransitionGate) {
			var predecessor = _primary;
			sides = Sides();
			try {
				TerminalizeConversations("Conversation interrupted by /clear.", sides);
				predecessor.SettleInteractions();
				_sessions.Replace(_definition.Id, _context.Workspace, [], []);
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, sides);
				throw;
			}
			var process = ReplaceableProcess(predecessor);
			var successor = Succeed(predecessor, process, handoff => CreatePrimary(handoff with {
				Continuation = NewContinuation(string.Empty, 0, string.Empty, guidanceSent: false),
			}));
			lock (_gate) _sides.Clear();
			foreach (var side in sides) side.Conversation.Retire();
			_sideConversations.Clear();
			_storageFailed = false;
			_displayRestored = true;
			successor.Publish(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
			Launch(successor, process);
		}
		foreach (var side in sides) DisposeSide(side);
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		SideRuntime[] sides;
		AcpConversation primary;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed) {
					return;
				}
				_disposed = true;
				primary = _primary;
				sides = [.. _sides.Values];
				_sides.Clear();
			}
			primary.SettleForDisposal();
			foreach (var side in sides) side.Conversation.SettleForDisposal();
			foreach (var side in sides) side.Conversation.Retire();
		}
		try {
			await primary.CloseAsync(async () => {
				var disposals = sides.Select(side => side.Conversation.CloseAsync(static () => Task.CompletedTask)).ToArray();
				await _connection.DisposeAsync().ConfigureAwait(false);
				await Task.WhenAll(disposals).ConfigureAwait(false);
			}).ConfigureAwait(false);
		} finally {
			await _context.Registry.DisposeAsync().ConfigureAwait(false);
		}
	}

	// The process starts synchronously inside Start or Restart, so this attaches the primary just installed.
	private void OnProcessStarted(AcpProcessGeneration process) {
		AcpConversation conversation;
		lock (_turnTransitionGate) {
			conversation = _primary;
			lock (_gate) (_processGeneration, _process) = (process.Generation, null);
			conversation.Attach(new AcpProcess(_connection, process.Generation));
		}
		conversation.RunRuntime(() => InitializeAsync(conversation, process.Generation));
	}

	private async Task InitializeAsync(AcpConversation conversation, long generation) {
		var features = AcpAgentFeatures.Read(await _connection.RequestAsync(
			"initialize",
			new JsonObject {
				["protocolVersion"] = 1,
				["clientCapabilities"] = new JsonObject {
					["auth"] = new JsonObject { ["terminal"] = true },
					["fs"] = new JsonObject { ["readTextFile"] = true, ["writeTextFile"] = true },
					["plan"] = new JsonObject(),
					["subagents"] = new JsonObject(),
					["terminal"] = true,
					["session"] = AcpInferenceClient.SessionCapabilities(),
					["elicitation"] = new JsonObject { ["form"] = new JsonObject(), ["url"] = new JsonObject() },
				},
				["clientInfo"] = AcpInferenceClient.ClientInfo(
					_context.Runtime.Build.ToString(System.Globalization.CultureInfo.InvariantCulture)),
			},
			generation,
			CancellationToken.None).ConfigureAwait(false));
		lock (_turnTransitionGate) {
			if (!conversation.Live) return;
			lock (_gate) (_features, _process) = (features, new AcpProcess(_connection, generation));
		}
		await conversation.OpenAsync(features).ConfigureAwait(false);
	}

	private void OnProtocolFault(long generation, Exception error) {
		lock (_turnTransitionGate) {
			// Attached conversations take their process's faults through their endpoints; a launch failure has none.
			if (!_primary.Attached && _connection.IsLatestGeneration(generation)) FailProcess(error);
		}
	}

	/// <summary>Fails the process and every conversation on it; false when the primary already ended.</summary>
	private bool FailProcess(Exception error) {
		lock (_turnTransitionGate) {
			var primary = _primary;
			if (!primary.Live) return false;
			if (_processGeneration > 0) {
				_connection.TerminateGeneration(
					_processGeneration,
					string.IsNullOrEmpty(error.Message) ? "ACP runtime failure." : error.Message);
			}
			foreach (var side in Sides()) side.Conversation.Terminate(error);
			primary.Terminate(error);
			return true;
		}
	}

	private void StopForStorageFailure(AcpSessionStoreException error, IReadOnlyList<SideRuntime> sides) {
		_storageFailed = true;
		_primary.MarkFailed();
		if (_processGeneration > 0) _connection.TerminateGeneration(_processGeneration, error.Message);
		TerminalizeConversations("Conversation interrupted by a storage failure.", sides);
		_primary.ReportFailure(error);
	}

	private void TerminalizeConversations(string summary, IReadOnlyList<SideRuntime> sides) {
		_primary.TerminalizeForRestart(clearSubmissions: true, summary);
		foreach (var side in sides) side.Conversation.TerminalizeForRestart(clearSubmissions: true, summary);
	}
}
