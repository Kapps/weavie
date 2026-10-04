using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private bool IsUntouchedPrimary => _role is PrimaryRole && _turnNumber == 0;

	/// <inheritdoc/>
	public IReadOnlyList<AgentPaneMessage> Restore() {
		if (_role is SideRole) throw new InvalidOperationException("Side conversations restore with their owner.");
		try {
			return RestoreDisplay();
		} catch (Exception error) {
			// Reported by Start, once the session's failure observers are wired.
			_restoreFailure = error;
			return [];
		}
	}

	/// <inheritdoc/>
	public void Start() {
		bool restored;
		lock (_gate) {
			if (_started) {
				return;
			}
			restored = _displayRestored;
			if (_role is PrimaryRole && !restored && _restoreFailure is null) {
				throw new InvalidOperationException("Restore the saved transcript before starting.");
			}
			_started = true;
		}
		if (_role is SideRole side) {
			OnProcessStarted(new AcpProcessGeneration(side.Generation, 0));
		} else if (!restored) {
			FailRuntime(_restoreFailure!); // The runtime stays failed until Restart retries the restore.
		} else {
			try {
				_connection.Start();
			} catch (Exception error) {
				FailRuntime(error);
			}
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		SideRuntime[] sideSessions;
		string? sessionId;
		bool close;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed) {
					return;
				}
				_disposed = true;
				_controlMutations.Clear();
				sideSessions = [.. _sideRuntimes.Values];
				sessionId = _endpoint?.SessionId;
				close = (_ready || _role is SideRole) && _features.Close && sessionId is not null;
			}
			SettleToolsForDisposal();
			foreach (var side in sideSessions) side.Session.SettleToolsForDisposal();
			lock (_gate) _sideRuntimes.Clear();
			foreach (var side in sideSessions) side.Session._port.Detach();
		}

		CancelPendingInteractions();
		AbandonClientRequests();
		Task<JsonElement>? closeRequest = null;
		if (close) {
			closeRequest = _endpoint?.CloseAsync();
		}
		_endpoint?.Retire();

		try {
			var disposals = sideSessions.Select(side => side.Session.DisposeAsync().AsTask()).ToArray();
			if (_role is PrimaryRole) await _connection.DisposeAsync().ConfigureAwait(false);
			await Task.WhenAll(disposals).ConfigureAwait(false);
		} finally {
			if (closeRequest is not null) {
				try {
					await closeRequest.ConfigureAwait(false);
				} catch (Exception ex) {
					_log($"[acp:{_definition.Id}] session/close ended during process teardown: {ex.Message}");
				}
			}
			try {
				await _terminals.DisposeAsync().ConfigureAwait(false);
			} finally {
				if (_role is PrimaryRole) await _context.Registry.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private void OnProcessStarted(AcpProcessGeneration process) {
		lock (_turnTransitionGate) {
			lock (_gate) {
				_activeGeneration = process.Generation;
				_endpoint = null;
				// An untouched primary has no conversation to resume; a side fork can have inherited history.
				if (IsUntouchedPrimary) _sessionId = null;
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
		_port.UsageChanged(Snapshot);
		RunRuntime(process.Generation, () => InitializeGenerationAsync(process));
	}

	private async Task InitializeGenerationAsync(AcpProcessGeneration process) {
		var features = _role is SideRole side ? side.Owner._features : AcpAgentFeatures.Read(await _connection.RequestAsync(
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
			process.Generation,
			CancellationToken.None).ConfigureAwait(false));
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != process.Generation) return;
				_features = features;
			}
		}
		await OpenSessionAsync(process.Generation).ConfigureAwait(false);
	}

	private async Task OpenSessionAsync(long generation) {
		string? sessionId;
		bool reconnecting;
		bool loadSession;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) return;
				reconnecting = _sessionId is not null;
				if (reconnecting && !_features.Load && !_features.Resume) {
					throw new AcpProtocolException(
						$"{_definition.Name} cannot restore this conversation. Start a new conversation to continue.");
				}
				string? persisted = _sessionId ?? _endpoint?.SessionId;
				sessionId = persisted is not null && (_features.Load || _features.Resume)
					? persisted
					: null;
				loadSession = sessionId is not null && _features.Load && !_features.Resume;
				_sessionOpening = true;
				_endpoint ??= _connection.OpenEndpoint(generation, sessionId, HandleNotification, RegisterClientRequest);
				if (sessionId is null && _role is PrimaryRole) _guidanceSent = false;

			}
		}

		JsonElement setup;
		try {
			if (_role is SideRole { Conversation.AnchorTurnNumber: > 0 } fork && sessionId is null) {
				await Endpoint(generation).ForkFromAsync(fork.Owner.Endpoint(generation), new {
					cwd = Path.GetFullPath(_context.Workspace),
					mcpServers = McpServers(),
				}).ConfigureAwait(false);
				lock (_turnTransitionGate) {
					if (!OwnsGeneration(generation)) return;
					sessionId = Endpoint(generation).SessionId;
					SaveContinuation();
				}
				loadSession = true;
			}
			if (sessionId is null) {
				lock (_gate) _planTurns.Clear();
				setup = await Endpoint(generation).CreateAsync(
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					}).ConfigureAwait(false);
				if (!OwnsGeneration(generation)) return;
				sessionId = Endpoint(generation).SessionId;
			} else if (loadSession) {
				lock (_turnTransitionGate) {
					if (!OwnsGeneration(generation)) return;
					lock (_gate) _loadingTranscript = true;
				}
				try {
					setup = await Endpoint(generation).RequestAsync(
						"session/load",
						new { cwd = Path.GetFullPath(_context.Workspace), mcpServers = McpServers() },
						CancellationToken.None).ConfigureAwait(false);
				} finally {
					lock (_turnTransitionGate) {
						lock (_gate) {
							if (OwnsGeneration(generation)) _loadingTranscript = false;
						}
					}
				}
			} else {
				setup = await Endpoint(generation).RequestAsync(
					"session/resume",
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					},
					CancellationToken.None).ConfigureAwait(false);
				if (!OwnsGeneration(generation)) return;
			}
		} catch (AcpRequestException ex) when (ex.Code == -32000) {
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (_disposed || _activeGeneration != generation) return;
					_sessionOpening = false;
				}
				if (SettleInterruptedSideOpening()) return;
				if (!_connection.ReportHealthy(generation)) {
					throw new AcpProtocolException("The ACP authentication generation is no longer current.");
				}
				RequestAuthentication(ex.Message, opensSession: true);
			}
			return;
		} catch {
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (_disposed || _activeGeneration != generation) return;
					_sessionOpening = false;
				}
			}
			throw;
		}

		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) return;
				_sessionId = sessionId;
				_sessionOpening = false;
				ReadControlStateLocked(setup);
			}
			SaveContinuation();
		}
		await RestoreControlDefaultsAsync(generation).ConfigureAwait(false);
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) return;
				_ready = true;
			}
			if (SettleInterruptedSideOpening()) return;
			if (!_connection.ReportHealthy(generation)) {
				throw new AcpProtocolException("The initialized ACP generation is no longer current.");
			}
			Observe(new AgentSessionStarted(reconnecting ? "restart" : "startup"));
			RestoreSetupActivity();
			RaiseControls();
			DispatchPendingSubmission();
		}
	}

	private bool SettleInterruptedSideOpening() {
		lock (_gate) if (_role is not SideRole || _pendingSubmissions.Count > 0) return false;
		FailConversationSerialized(new InvalidOperationException("Side conversation interrupted."));
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
			ProviderId = _definition.Id,
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
