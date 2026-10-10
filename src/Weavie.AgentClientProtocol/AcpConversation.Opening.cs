using System.Text.Json;
using System.Text.Json.Nodes;
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
				_endpoint.Value.Open();
				if (sessionId is not null) _endpoint.Value.Bind(sessionId);
				if (sessionId is null && !_spec.SideScoped && _spec.Opening is not ForkFromOpening) _guidanceSent = false;
			}
		}

		JsonElement setup;
		try {
			if (_spec.Opening is ForkFromOpening fork && sessionId is null) {
				await _endpoint.Value.ForkFromAsync(fork.Parent._endpoint.Value, ForkParameters(fork.MessageId)).ConfigureAwait(false);
				lock (_turnTransitionGate) {
					if (!Live) return;
					sessionId = _endpoint.Value.SessionId;
					SaveContinuation();
				}
				loadSession = true;
			}
			if (sessionId is null) {
				lock (_gate) _planTurns.Clear();
				setup = await _endpoint.Value.CreateAsync(SessionParameters()).ConfigureAwait(false);
				if (!Live) return;
				sessionId = _endpoint.Value.SessionId;
			} else if (loadSession) {
				lock (_turnTransitionGate) {
					if (!Live) return;
					lock (_gate) _loadingTranscript = true;
				}
				try {
					setup = await _endpoint.Value.RestoreAsync("session/load", SessionParameters()).ConfigureAwait(false);
				} finally {
					lock (_turnTransitionGate) {
						if (Live) lock (_gate) _loadingTranscript = false;
					}
				}
			} else {
				setup = await _endpoint.Value.RestoreAsync("session/resume", SessionParameters()).ConfigureAwait(false);
				if (!Live) return;
			}
		} catch (AcpRequestException ex) when (ex.Code == -32000 && !ProvesForkPoint) {
			lock (_turnTransitionGate) {
				if (!Live) return;
				lock (_gate) _sessionOpening = false;
				if (SettleInterruptedSideOpening()) return;
				if (!_endpoint.Value.ReportHealthy()) {
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
		if (_spec.Opening is ForkFromOpening { MessageId: { } messageId } && !_replay.EndsAt(messageId)) {
			throw new InvalidOperationException($"{Definition.Name} did not rewind to the requested message.");
		}

		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) {
				_sessionId = sessionId;
				_sessionOpening = false;
				ReadControlStateLocked(setup);
			}
			_port.Opened();
			if (!Live) return; // An owner that cannot adopt this conversation restarts onto its continuation.
			SaveContinuation();
		}
		await RestoreControlDefaultsAsync().ConfigureAwait(false);
		lock (_turnTransitionGate) {
			if (!Live) return;
			lock (_gate) _ready = true;
			if (SettleInterruptedSideOpening()) return;
			if (!_endpoint.Value.ReportHealthy()) {
				throw new AcpProtocolException("The initialized ACP generation is no longer current.");
			}
			Observe(new AgentSessionStarted(reconnecting ? "restart" : "startup"));
			RestoreSetupActivity();
			RaiseControls();
			DispatchPendingSubmission();
		}
	}

	// A rewind forks an authenticated process to prove a point there, so a sign-in request fails it instead.
	private bool ProvesForkPoint => _spec.Opening is ForkFromOpening { MessageId: not null };

	// ACP has no capability flag for the fork point, so the fork's replay proves the agent honoured it.
	private JsonObject ForkParameters(string? messageId) {
		var parameters = SessionParameters();
		if (messageId is not null) {
			parameters["_meta"] = new JsonObject {
				["jetbrains"] = new JsonObject {
					["air"] = new JsonObject { ["fork"] = new JsonObject { ["version"] = 1, ["messageId"] = messageId } },
				},
			};
		}
		return parameters;
	}

	private sealed class RewindReplay {
		private string? _lastAgentMessage;
		private bool _userAfterAgent;

		public void Observe(string kind, JsonElement update) {
			switch (kind) {
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

	// A freshly opened session owns no running tool call — a new incarnation starts with none and a replayed one
	// is history — so the only setup state to restore is a request still waiting on the user.
	private void RestoreSetupActivity() {
		bool requiresInput;
		lock (_gate) {
			requiresInput = HasPendingInteractionLocked();
		}
		if (requiresInput) Observe(new AgentInputResolved(RequiresUserInput: true));
	}

	private JsonObject SessionParameters() => AcpContent.Session(Path.GetFullPath(_context.Workspace), McpServers());

	private JsonArray McpServers() {
		if (_features.HttpMcp) {
			return new JsonArray(new JsonObject {
				["type"] = "http",
				["name"] = "weavie",
				["url"] = _context.Registry.StreamableHttpUrl,
				["headers"] = new JsonArray(NameValue("Authorization", "Bearer " + _context.Registry.Credential.Token)),
			});
		}
		return new JsonArray(new JsonObject {
			["type"] = "stdio",
			["name"] = "weavie",
			["command"] = McpProxyBinary.PathIn(AppContext.BaseDirectory),
			["args"] = new JsonArray(),
			["env"] = new JsonArray(
				NameValue("WEAVIE_MCP_URL", _context.Registry.StreamableHttpUrl),
				NameValue("WEAVIE_MCP_TOKEN", _context.Registry.Credential.Token)),
		});
	}

	private static JsonObject NameValue(string name, string value) => new() { ["name"] = name, ["value"] = value };

}
