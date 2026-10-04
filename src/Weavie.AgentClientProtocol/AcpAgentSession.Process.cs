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
			long generation = _primary.Generation;
			var sides = Sides();
			try {
				_primary.TerminalizeForRestart(clearSubmissions, "ACP agent restarted.");
				SuspendSides("ACP agent restarted.");
				_primary.SettleInteractions();
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, generation, sides);
				throw;
			}
			// The predecessor dies before the restart fails its pending requests.
			ReplacePrimary(_primary.Retire());
			_connection.Restart();
		}
	}

	/// <inheritdoc/>
	public void StartNewConversation() {
		SideRuntime[] sides;
		lock (_turnTransitionGate) {
			long generation = _primary.Generation;
			sides = Sides();
			try {
				TerminalizeConversations("Conversation interrupted by /clear.", sides);
				_primary.SettleInteractions();
				_sessions.Replace(_definition.Id, _context.Workspace, [], []);
			} catch (AcpSessionStoreException error) {
				StopForStorageFailure(error, generation, sides);
				throw;
			}
			ReplacePrimary(_primary.Retire() with { Continuation = NewContinuation(string.Empty, 0, string.Empty, guidanceSent: false) });
			lock (_gate) _sides.Clear();
			foreach (var side in sides) side.Conversation.Retire();
			_sideConversations.Clear();
			_storageFailed = false;
			_displayRestored = true;
			_primary.Publish(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
			_connection.Restart();
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
			conversation.Attach(process.Generation);
		}
		conversation.RunRuntime(process.Generation, () => InitializeAsync(conversation, process.Generation));
	}

	private async Task InitializeAsync(AcpConversation conversation, long generation) {
		var features = AcpAgentFeatures.Read(await _connection.RequestAsync(
			"initialize",
			new {
				protocolVersion = 1,
				clientCapabilities = new {
					auth = new { terminal = true },
					fs = new { readTextFile = true, writeTextFile = true },
					plan = new { },
					terminal = true,
					session = new { configOptions = new { boolean = new { } } },
					elicitation = new { form = new { }, url = new { } },
				},
				clientInfo = new {
					name = "weavie",
					title = "Weavie",
					version = _context.Runtime.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
				},
			},
			generation,
			CancellationToken.None).ConfigureAwait(false));
		lock (_turnTransitionGate) {
			if (!conversation.OwnsGeneration(generation)) return;
			lock (_gate) _features = features;
		}
		await conversation.OpenAsync(features, generation).ConfigureAwait(false);
	}

	private void OnProtocolFault(long generation, Exception error) {
		lock (_turnTransitionGate) {
			long active = _primary.Generation;
			if (active == generation || active == 0 && _connection.IsLatestGeneration(generation)) FailProcess(error);
		}
	}

	/// <summary>Fails the process and every conversation on it; false when the primary already ended.</summary>
	private bool FailProcess(Exception error) {
		lock (_turnTransitionGate) {
			var primary = _primary;
			if (!primary.Live) return false;
			long generation = primary.Generation;
			if (generation > 0) {
				_connection.TerminateGeneration(
					generation,
					string.IsNullOrEmpty(error.Message) ? "ACP runtime failure." : error.Message);
			}
			foreach (var side in Sides()) side.Conversation.Terminate(error);
			primary.Terminate(error);
			return true;
		}
	}

	private void StopForStorageFailure(AcpSessionStoreException error, long generation, IReadOnlyList<SideRuntime> sides) {
		_storageFailed = true;
		_primary.MarkFailed();
		if (generation > 0) _connection.TerminateGeneration(generation, error.Message);
		TerminalizeConversations("Conversation interrupted by a storage failure.", sides);
		_primary.ReportFailure(error);
	}

	private void TerminalizeConversations(string summary, IReadOnlyList<SideRuntime> sides) {
		_primary.TerminalizeForRestart(clearSubmissions: true, summary);
		foreach (var side in sides) side.Conversation.TerminalizeForRestart(clearSubmissions: true, summary);
	}
}
