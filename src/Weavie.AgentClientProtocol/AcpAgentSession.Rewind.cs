using System.Globalization;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession : IStructuredAgentRewind {
	private bool _rewinding;

	private bool ForkableLocked => _role is PrimaryRole && _ready && _features.Fork && _features.Load;

	private bool RewindableLocked => ForkableLocked && _turnNumber > 0;

	/// <inheritdoc/>
	public Task RewindLatestAsync() => RewindBeforeAsync(TurnId());

	/// <inheritdoc/>
	public async Task RewindBeforeAsync(string turnId) {
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		AcpRewindPlan plan;
		long generation;
		lock (_turnTransitionGate) {
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (!ForkableLocked) throw new InvalidOperationException($"{_definition.Name} cannot rewind this conversation now.");
				if (_rewinding || _sessionOpening || _pendingSubmissions.Count > 0 || _promptActive || HasBackgroundWorkLocked()
					|| HasPendingInteractionLocked() || _sideRuntimes.Values.Any(side => side.Session.HasWork())) {
					throw new InvalidOperationException("Wait for the agent to finish before rewinding.");
				}
				generation = _activeGeneration;
			}
			plan = AcpRewindPlan.Create(_sessions.ReadMessages(_definition.Id, _context.Workspace), turnId);
			lock (_gate) _rewinding = true;
		}
		try {
			string? sessionId = plan.ForkMessageId is { } messageId ? await ForkAtAsync(generation, messageId).ConfigureAwait(false) : null;
			lock (_turnTransitionGate) {
				if (!OwnsGeneration(generation)) throw new InvalidOperationException("The agent restarted during the rewind.");
				CommitRewind(plan, sessionId, generation);
			}
		} finally {
			lock (_gate) _rewinding = false;
			DispatchPendingSubmission();
		}
	}

	// ACP has no capability flag for the fork point, so the branch's replay proves the agent honoured it.
	private async Task<string> ForkAtAsync(long generation, string messageId) {
		var replay = new RewindReplay();
		var branch = _connection.OpenEndpoint(generation, null, (_, root) => replay.Observe(root), _connection.RejectClosedRequest);
		string cwd = Path.GetFullPath(_context.Workspace);
		try {
			await branch.ForkFromAsync(Endpoint(generation), new {
				cwd,
				mcpServers = McpServers(),
				_meta = new { jetbrains = new { air = new { fork = new { version = 1, messageId } } } },
			}).ConfigureAwait(false);
			await branch.RequestAsync("session/load", new { cwd, mcpServers = McpServers() }, CancellationToken.None).ConfigureAwait(false);
			if (!replay.EndsAt(messageId)) throw new InvalidOperationException($"{_definition.Name} did not rewind to the requested message.");
			branch.Retire(); // The commit restarts onto the fork; this endpoint only had to prove it.
			return branch.SessionId!;
		} catch {
			if (_features.Close && branch.SessionId is not null) await branch.CloseAsync().ConfigureAwait(false);
			else branch.Retire();
			throw;
		}
	}

	private void CommitRewind(AcpRewindPlan plan, string? sessionId, long generation) {
		SideRuntime[] sides;
		lock (_gate) sides = [.. _sideRuntimes.Values];
		AcpConversationState primary;
		string[] dropped;
		try {
			TerminalizeForRestart(clearSubmissions: false, "Conversation rewound.");
			SuspendSideRuntimes("Conversation rewound.");
			var state = ContinuationState();
			primary = state with {
				SessionId = sessionId,
				TurnNumber = plan.Turn - 1,
				GuidanceSent = sessionId is not null && state.GuidanceSent,
				PlanTurns = state.PlanTurns.Where(pair => plan.Keeps(long.Parse(pair.Value, CultureInfo.InvariantCulture)))
					.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
			};
			dropped = [.. _sideConversations.Values.Where(side => !plan.Keeps(side.AnchorTurnNumber)).Select(side => side.ConversationId)];
			_sessions.Replace(_definition.Id, _context.Workspace, [primary, .. _sideConversations.Values.Where(side => !dropped.Contains(side.ConversationId))], plan.Kept);
		} catch (AcpSessionStoreException error) {
			StopForStorageFailure(error, generation, sides);
			throw;
		}
		foreach (string conversationId in dropped) _sideConversations.Remove(conversationId);
		lock (_gate) RestoreContinuation(primary);
		PaneSnapshot?.Invoke(plan.Kept);
		_connection.Restart();
		if (plan.Prompt.Length > 0) PrefillPrompt(plan.Prompt);
	}

	private sealed class RewindReplay {
		private string? _lastAgentMessage;
		private bool _userAfterAgent;

		public void Observe(JsonElement root) {
			if (!root.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("update", out var update)) return;
			switch (OptionalString(update, "sessionUpdate")) {
				case "agent_message_chunk":
					_lastAgentMessage = OptionalString(update, "messageId");
					_userAfterAgent = false;
					break;
				case "user_message_chunk":
					_userAfterAgent = true;
					break;
			}
		}

		public bool EndsAt(string messageId) => !_userAfterAgent && _lastAgentMessage == messageId;
	}
}
