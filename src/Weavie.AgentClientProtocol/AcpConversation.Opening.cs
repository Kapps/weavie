using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	internal async Task OpenAsync(AcpAgentFeatures features) {
		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) _features = features;
		}
		await OpenSessionAsync().ConfigureAwait(false);
	}

	private async Task OpenSessionAsync() {
		string? sessionId;
		bool reconnecting;
		bool loadSession;
		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) {
				reconnecting = _sessionId is not null;
				if (reconnecting && !_features.Load && !_features.Resume) {
					throw new AcpProtocolException(
						$"{Definition.Name} cannot restore this conversation. Start a new conversation to continue.");
				}
				string? persisted = _sessionId ?? _endpoint.Value.SessionId;
				sessionId = persisted is not null && (_features.Load || _features.Resume)
					? persisted
					: null;
				loadSession = sessionId is not null && _features.Load && !_features.Resume;
				_sessionOpening = true;
				if (sessionId is not null) _endpoint.Value.Bind(sessionId);
				if (sessionId is null && !_spec.SideScoped) _guidanceSent = false;
			}
		}

		JsonElement setup;
		try {
			if (_spec.Opening is ForkFromOpening fork && sessionId is null) {
				await _endpoint.Value.ForkFromAsync(fork.Parent._endpoint.Value, new {
					cwd = Path.GetFullPath(_context.Workspace),
					mcpServers = McpServers(),
				}).ConfigureAwait(false);
				lock (_turnTransitionGate) {
					if (!Live) return;
					sessionId = _endpoint.Value.SessionId;
					SaveContinuation();
				}
				loadSession = true;
			}
			if (sessionId is null) {
				lock (_gate) _planTurns.Clear();
				setup = await _endpoint.Value.CreateAsync(
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					}).ConfigureAwait(false);
				if (!Live) return;
				sessionId = _endpoint.Value.SessionId;
			} else if (loadSession) {
				lock (_turnTransitionGate) {
					if (!Live) return;
					lock (_gate) _loadingTranscript = true;
				}
				try {
					setup = await _endpoint.Value.RequestAsync(
						"session/load",
						new { cwd = Path.GetFullPath(_context.Workspace), mcpServers = McpServers() },
						CancellationToken.None).ConfigureAwait(false);
				} finally {
					lock (_turnTransitionGate) {
						if (Live) lock (_gate) _loadingTranscript = false;
					}
				}
			} else {
				setup = await _endpoint.Value.RequestAsync(
					"session/resume",
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					},
					CancellationToken.None).ConfigureAwait(false);
				if (!Live) return;
			}
		} catch (AcpRequestException ex) when (ex.Code == -32000) {
			lock (_turnTransitionGate) {
				if (!Live) return;
				lock (_gate) _sessionOpening = false;
				if (SettleInterruptedSideOpening()) return;
				if (!_connection.ReportHealthy(_endpoint.Value.Generation)) {
					throw new AcpProtocolException("The ACP authentication generation is no longer current.");
				}
				RequestAuthentication(ex.Message, opensSession: true);
			}
			return;
		} catch {
			lock (_turnTransitionGate) {
				if (!Live) return;
				lock (_gate) _sessionOpening = false;
			}
			throw;
		}

		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) {
				_sessionId = sessionId;
				_sessionOpening = false;
				ReadControlStateLocked(setup);
			}
			SaveContinuation();
		}
		await RestoreControlDefaultsAsync(_endpoint.Value.Generation).ConfigureAwait(false);
		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) _ready = true;
			if (SettleInterruptedSideOpening()) return;
			if (!_connection.ReportHealthy(_endpoint.Value.Generation)) {
				throw new AcpProtocolException("The initialized ACP generation is no longer current.");
			}
			Observe(new AgentSessionStarted(reconnecting ? "restart" : "startup"));
			RestoreSetupActivity();
			RaiseControls();
			DispatchPendingSubmission();
		}
	}

	// ACP has no capability flag for the fork point, so the branch's replay proves the agent honoured it.
	internal async Task<string> ForkAtAsync(string messageId) {
		var replay = new RewindReplay();
		var branch = _connection.OpenEndpoint(_endpoint.Value.Generation, null, (_, root) => replay.Observe(root), _connection.RejectClosedRequest);
		string cwd = Path.GetFullPath(_context.Workspace);
		try {
			await branch.ForkFromAsync(_endpoint.Value, new {
				cwd,
				mcpServers = McpServers(),
				_meta = new { jetbrains = new { air = new { fork = new { version = 1, messageId } } } },
			}).ConfigureAwait(false);
			await branch.RequestAsync("session/load", new { cwd, mcpServers = McpServers() }, CancellationToken.None).ConfigureAwait(false);
			if (!replay.EndsAt(messageId)) throw new InvalidOperationException($"{Definition.Name} did not rewind to the requested message.");
			branch.Retire(); // The commit restarts onto the fork; this endpoint only had to prove it.
			return branch.SessionId!;
		} catch {
			if (_features.Close && branch.SessionId is not null) await branch.CloseAsync().ConfigureAwait(false);
			else branch.Retire();
			throw;
		}
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

	private bool SettleInterruptedSideOpening() {
		lock (_gate) if (!_spec.SideScoped || _pendingSubmissions.Count > 0) return false;
		Terminate(new InvalidOperationException("Side conversation interrupted."));
		return true;
	}

	// A freshly opened session owns no running tool call — the generation reset cleared them and a replayed one
	// is history — so the only setup state to restore is a request still waiting on the user.
	private void RestoreSetupActivity() {
		bool requiresInput;
		lock (_gate) {
			requiresInput = HasPendingInteractionLocked();
		}
		if (requiresInput) Observe(new AgentInputResolved(RequiresUserInput: true));
	}

	private void RequestAuthentication(string message, bool opensSession) {
		if (_features.AuthMethods.Count == 0) {
			throw new AcpProtocolException("The ACP agent requires authentication but advertised no auth methods.");
		}
		string itemId;
		lock (_gate) {
			if (_authenticationPending) {
				throw new AcpProtocolException("The ACP agent requested authentication more than once.");
			}
			_authenticationPending = true;
			_authenticating = false;
			_authenticationOpensSession = opensSession;
			itemId = $"authentication:{++_authenticationSequence}";
			_authenticationItemId = itemId;
		}
		Observe(new AgentInputRequested());
		Observe(new AgentInputResolved(RequiresUserInput: true));
		Emit(new AgentPaneMessage {
			Type = "authentication-requested",
			ProviderId = Definition.Id,
			ThreadId = SessionId(),
			ItemId = itemId,
			RequestId = itemId,
			ItemType = "authentication",
			Summary = message,
			Actions = [.. _features.AuthMethods.Select(method => new AgentActionOption {
				Id = method.Id,
				Label = method.Name,
				Kind = "authenticate",
			})],
			Status = "pending",
		});
	}

	private object[] McpServers() {
		if (_features.HttpMcp) {
			return [new {
				type = "http",
				name = "weavie",
				url = _context.Registry.StreamableHttpUrl,
				headers = new[] {
					new { name = "Authorization", value = "Bearer " + _context.Registry.Credential.Token },
				},
			}];
		}
		return [new {
			type = "stdio",
			name = "weavie",
			command = McpProxyBinary.PathIn(AppContext.BaseDirectory),
			args = Array.Empty<string>(),
			env = new[] {
				new { name = "WEAVIE_MCP_URL", value = _context.Registry.StreamableHttpUrl },
				new { name = "WEAVIE_MCP_TOKEN", value = _context.Registry.Credential.Token },
			},
		}];
	}

}
