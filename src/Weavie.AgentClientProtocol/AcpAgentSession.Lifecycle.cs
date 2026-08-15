using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Start() {
		lock (_gate) {
			if (_started) {
				return;
			}
			_started = true;
		}
		_connection.Start();
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		string? sessionId;
		bool close;
		long generation;
		lock (_gate) {
			if (_disposed) {
				return;
			}
			_disposed = true;
			_controlMutations.Clear();
			sessionId = _sessionId;
			close = _ready && _supportsClose && sessionId is not null;
			generation = _activeGeneration;
		}

		CancelPendingInteractions();
		AbandonClientRequests();
		Task<JsonElement>? closeRequest = null;
		if (close) {
			closeRequest = _connection.RequestAsync(
				"session/close",
				new { sessionId },
				generation,
				CancellationToken.None);
		}

		try {
			await _connection.DisposeAsync().ConfigureAwait(false);
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
				await _context.Registry.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private void OnProcessStarted(AcpProcessGeneration process) {
		lock (_turnTransitionGate) {
			lock (_gate) {
				_activeGeneration = process.Generation;
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
				_loadedMessages.Clear();
				_controls.Clear();
				_commands = [];
				_tools.Clear();
				_activeTools.Clear();
				_content.Clear();
				_turnItemIds.Clear();
				_replayContentRole = null;
				_contextUsage = null;
				_usageLimits.Clear();
			}
		}
		CancelPendingInteractions();
		AbandonClientRequests();
		RaiseControls();
		UsageChanged?.Invoke(Snapshot);
		RunRuntime(process.Generation, () => InitializeGenerationAsync(process));
	}

	private async Task InitializeGenerationAsync(AcpProcessGeneration process) {
		var initialized = await _connection.RequestAsync(
			"initialize",
			new {
				protocolVersion = 1,
				clientCapabilities = new {
					auth = new { terminal = true },
					fs = new { readTextFile = true, writeTextFile = true },
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
			CancellationToken.None).ConfigureAwait(false);
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != process.Generation) return;
				ReadCapabilities(initialized);
				ReadAuthMethods(initialized);
			}
		}
		await OpenSessionAsync(process.Generation).ConfigureAwait(false);
	}

	private async Task OpenSessionAsync(long generation) {
		string? sessionId;
		bool reconnecting;
		bool resetTranscript;
		bool loadSession;
		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) return;
				reconnecting = _sessionId is not null;
				string? persisted = _sessionId ?? _sessions.Resolve(_definition.Id, _context.Workspace);
				sessionId = persisted is not null && (_supportsLoad || _supportsResume)
					? persisted
					: null;
				loadSession = sessionId is not null && _supportsLoad && (!reconnecting || !_supportsResume);
				resetTranscript = persisted is not null && sessionId is null;
				_openingSessionId = sessionId;
				_sessionOpening = true;
				if (sessionId is null) _guidanceSent = false;
				else if (!loadSession) {
					_turnNumber = _sessions.ResolveTurnNumber(_definition.Id, _context.Workspace);
				}
			}
		}
		if (resetTranscript) {
			_sessions.Clear(_definition.Id, _context.Workspace);
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (_disposed || _activeGeneration != generation) return;
					_sessionId = null;
					_turnNumber = 0;
				}
				Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
			}
		}

		JsonElement setup;
		try {
			if (sessionId is null) {
				setup = await _connection.RequestAsync(
					"session/new",
					new {
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					},
					generation,
					CancellationToken.None).ConfigureAwait(false);
				lock (_turnTransitionGate) {
					lock (_gate) {
						if (_disposed || _activeGeneration != generation) return;
						string createdSessionId = RequiredString(setup, "sessionId", "session/new response");
						if (_openingSessionId is { } announced
							&& !string.Equals(announced, createdSessionId, StringComparison.Ordinal)) {
							throw new AcpProtocolException(
								$"session/new announced '{announced}' but returned '{createdSessionId}'.");
						}
						_openingSessionId = createdSessionId;
						sessionId = createdSessionId;
					}
				}
			} else if (loadSession) {
				lock (_turnTransitionGate) {
					lock (_gate) {
						if (_disposed || _activeGeneration != generation) return;
						_loadingTranscript = true;
						_turnNumber = 0;
						_replayContentRole = null;
						_loadedMessages.Clear();
					}
				}
				bool loaded = false;
				try {
					setup = await _connection.RequestAsync(
						"session/load",
						new {
							sessionId,
							cwd = Path.GetFullPath(_context.Workspace),
							mcpServers = McpServers(),
						},
						generation,
						CancellationToken.None).ConfigureAwait(false);
					lock (_turnTransitionGate) {
						lock (_gate) {
							if (_disposed || _activeGeneration != generation) return;
							loaded = true;
						}
						CompleteContentStreams();
					}
				} finally {
					IReadOnlyList<AgentPaneMessage>? snapshot = null;
					lock (_turnTransitionGate) {
						lock (_gate) {
							if (!_disposed && _activeGeneration == generation) {
								_loadingTranscript = false;
								if (loaded) {
									snapshot = [.. _loadedMessages];
								}
								_loadedMessages.Clear();
							}
						}
						if (snapshot is not null) {
							PaneSnapshot?.Invoke(snapshot);
						}
					}
				}
			} else {
				setup = await _connection.RequestAsync(
					"session/resume",
					new {
						sessionId,
						cwd = Path.GetFullPath(_context.Workspace),
						mcpServers = McpServers(),
					},
					generation,
					CancellationToken.None).ConfigureAwait(false);
				if (!OwnsGeneration(generation)) return;
			}
		} catch (AcpRequestException ex) when (ex.Code == -32000) {
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (_disposed || _activeGeneration != generation) return;
					_openingSessionId = null;
					_sessionOpening = false;
				}
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
					_openingSessionId = null;
					_sessionOpening = false;
				}
			}
			throw;
		}
		if (loadSession) {
			_sessions.Adopt(
				_definition.Id,
				_context.Workspace,
				sessionId ?? throw new AcpProtocolException("ACP session setup returned no session id."),
				_turnNumber);
		}

		lock (_turnTransitionGate) {
			lock (_gate) {
				if (_disposed || _activeGeneration != generation) {
					_openingSessionId = null;
					_sessionOpening = false;
					return;
				}
				_sessionId = sessionId;
				_openingSessionId = null;
				_sessionOpening = false;
				ReadControlStateLocked(setup);
				_ready = true;
			}
			if (!_connection.ReportHealthy(generation)) {
				throw new AcpProtocolException("The initialized ACP generation is no longer current.");
			}
			Observe(new AgentSessionStarted(reconnecting ? "restart" : "startup"));
			RestoreSetupActivity();
			RaiseControls();
			FlushPendingSubmissions();
		}
	}

	private void RestoreSetupActivity() {
		AcpToolState[] unobserved;
		bool active;
		bool requiresInput;
		lock (_gate) {
			unobserved = [.. _activeTools.Select(id => _tools[id]).Where(tool => !tool.StartedObserved)];
			active = HasBackgroundWorkLocked();
			_waitingForBackground = active;
			requiresInput = HasPendingInteractionLocked();
		}
		foreach (var tool in unobserved) EnsureObservedMutation(tool);
		if (active) Observe(new AgentTurnStopped(WillResume: true));
		if (requiresInput) Observe(new AgentInputResolved(RequiresUserInput: true));
	}

	private void RequestAuthentication(string message, bool opensSession) {
		if (_authMethods.Count == 0) {
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
			Actions = [.. _authMethods.Select(method => new AgentActionOption {
				Id = method.Id,
				Label = method.Name,
				Kind = "authenticate",
			})],
			Status = "pending",
		});
	}

	private object[] McpServers() {
		if (_supportsHttpMcp) {
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

	private void ReadCapabilities(JsonElement initialized) {
		if (!initialized.TryGetProperty("protocolVersion", out var version)
			|| !version.TryGetInt32(out int protocolVersion)
			|| protocolVersion != 1) {
			throw new AcpProtocolException("The ACP agent did not negotiate stable protocol version 1.");
		}
		JsonElement capabilities = default;
		if (initialized.TryGetProperty("agentCapabilities", out var advertised)
			&& advertised.ValueKind != JsonValueKind.Null) {
			if (advertised.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("ACP agentCapabilities must be an object when present.");
			}
			capabilities = advertised;
		}

		_supportsLoad = ReadBool(capabilities, "loadSession");
		_supportsClose = HasObject(capabilities, "sessionCapabilities", "close");
		_supportsResume = HasObject(capabilities, "sessionCapabilities", "resume");
		_supportsImages = ReadBool(capabilities, "promptCapabilities", "image");
		_supportsEmbeddedContext = ReadBool(capabilities, "promptCapabilities", "embeddedContext");
		_supportsHttpMcp = ReadBool(capabilities, "mcpCapabilities", "http");
		_supportsSteering = initialized.TryGetProperty("_meta", out var meta)
			&& ReadBool(meta, "steering", "supported");
	}

	private void ReadAuthMethods(JsonElement initialized) {
		if (!initialized.TryGetProperty("authMethods", out var methods) || methods.ValueKind == JsonValueKind.Null) {
			_authMethods = [];
			return;
		}
		if (methods.ValueKind != JsonValueKind.Array) {
			throw new AcpProtocolException("ACP authMethods must be an array when present.");
		}
		var ids = new HashSet<string>(StringComparer.Ordinal);
		var parsed = new List<AcpAuthMethod>();
		foreach (var method in methods.EnumerateArray()) {
			if (method.ValueKind != JsonValueKind.Object
				|| OptionalString(method, "id") is not { Length: > 0 } id
				|| OptionalString(method, "name") is not { Length: > 0 } name) continue;
			if (!ids.Add(id)) throw new AcpProtocolException($"ACP repeated auth method '{id}'.");
			string type = OptionalString(method, "type") ?? "agent";
			if (type is not ("agent" or "terminal")) continue;
			parsed.Add(new AcpAuthMethod(
				id,
				name,
				OptionalString(method, "description"),
				type,
				ReadAuthArguments(method),
				ReadAuthEnvironment(method)));
		}
		_authMethods = parsed;
	}

	private static IReadOnlyList<string> ReadAuthArguments(JsonElement method) {
		if (!method.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array) return [];
		var result = new List<string>();
		foreach (var argument in args.EnumerateArray()) {
			if (argument.ValueKind != JsonValueKind.String) return [];
			result.Add(argument.GetString()!);
		}
		return result;
	}

	private static IReadOnlyDictionary<string, string> ReadAuthEnvironment(JsonElement method) {
		if (!method.TryGetProperty("env", out var environment) || environment.ValueKind != JsonValueKind.Object) {
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var entry in environment.EnumerateObject()) {
			if (entry.Name.Length == 0 || entry.Value.ValueKind != JsonValueKind.String) {
				return new Dictionary<string, string>(StringComparer.Ordinal);
			}
			result.Add(entry.Name, entry.Value.GetString()!);
		}
		return result;
	}

	private static bool ReadBool(JsonElement parent, string child, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(child, out var value) && ReadBool(value, property);

	private static bool ReadBool(JsonElement parent, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(property, out var value)
		&& value.ValueKind is JsonValueKind.True or JsonValueKind.False
		&& value.GetBoolean();

	private static bool HasObject(JsonElement parent, string child, string property) =>
		parent.ValueKind == JsonValueKind.Object
		&& parent.TryGetProperty(child, out var value)
		&& value.ValueKind == JsonValueKind.Object
		&& value.TryGetProperty(property, out var result)
		&& result.ValueKind == JsonValueKind.Object;

	private static string RequiredString(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The {source} is missing '{property}'.");

	private static string RequiredText(JsonElement value, string property, string source) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			? result.GetString()!
			: throw new AcpProtocolException($"The {source} is missing string '{property}'.");

	private static string? OptionalString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			? result.GetString()
			: null;
}
