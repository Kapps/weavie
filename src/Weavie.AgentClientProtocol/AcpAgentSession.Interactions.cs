using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void ResolvePermission(string requestId, string optionId) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(optionId);
		if (!_pendingRequests.TryGetValue(requestId, out var pending) || pending.Kind != "permission") {
			EmitStaleInteraction(requestId, "permission");
			return;
		}
		var option = pending.Data.EnumerateArray().FirstOrDefault(candidate =>
			string.Equals(OptionalString(candidate, "optionId"), optionId, StringComparison.Ordinal));
		if (option.ValueKind == System.Text.Json.JsonValueKind.Undefined) {
			EmitFailure(new AcpProtocolException($"'{optionId}' was not advertised for permission {requestId}."));
			return;
		}
		if (!CompleteDeferredClientRequest(
			requestId,
			new { outcome = new { outcome = "selected", optionId } })) {
			EmitStaleInteraction(requestId, "permission");
			return;
		}
		ResolveInteraction(requestId, "approval-resolved", PermissionStatus(option), permission: true);
	}

	/// <inheritdoc/>
	public void ResolveInput(
		string requestId,
		string action,
		IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(action);
		ArgumentNullException.ThrowIfNull(answers);
		if (action is not ("accept" or "decline" or "cancel")) {
			EmitFailure(new AcpProtocolException($"Unsupported ACP elicitation action '{action}'."));
			return;
		}
		if (!_pendingRequests.TryGetValue(requestId, out var pending) || pending.Kind is not ("input" or "url")) {
			EmitStaleInteraction(requestId, "input");
			return;
		}
		Dictionary<string, object>? content = null;
		try {
			if (action == "accept") content = pending.Kind == "input"
				? BuildElicitationContent(pending.Data, answers)
				: new Dictionary<string, object>(StringComparer.Ordinal);
		} catch (AcpProtocolException ex) {
			EmitFailure(ex);
			return;
		}
		object response = action == "accept" ? new { action, content } : new { action };
		if (!CompleteDeferredClientRequest(requestId, response)) {
			EmitStaleInteraction(requestId, "input");
			return;
		}
		ResolveInteraction(
			requestId,
			"input-resolved",
			action == "accept" ? "accepted" : action,
			permission: false);
	}

	/// <inheritdoc/>
	public void Authenticate(string methodId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(methodId);
		ArgumentNullException.ThrowIfNull(answers);
		var method = _authMethods.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, methodId, StringComparison.Ordinal));
		if (method is null) {
			EmitFailure(new AcpProtocolException($"'{methodId}' is not an advertised ACP authentication method."));
			return;
		}
		bool authenticate;
		long generation;
		bool opensSession;
		CancellationTokenSource? cancellation = null;
		lock (_gate) {
			authenticate = _authenticationPending && !_authenticating;
			generation = _activeGeneration;
			opensSession = _authenticationOpensSession;
			if (authenticate) {
				_authenticating = true;
				cancellation = new CancellationTokenSource();
				_authenticationCancellation = cancellation;
			}
		}
		if (!authenticate) {
			EmitStaleInteraction("authentication", "authentication");
			return;
		}
		var authenticationCancellation = cancellation!;
		Run(async () => {
			using (authenticationCancellation) {
				try {
					if (method.Type == "agent") {
						await _connection.RequestAsync(
							"authenticate",
							new { methodId },
							authenticationCancellation.Token).ConfigureAwait(false);
					} else {
						var exit = await _context.AuthenticationTerminal.RunAsync(
							AuthenticationLaunch(method),
							authenticationCancellation.Token).ConfigureAwait(false);
						if (exit.ExitCode != 0) {
							throw new InvalidOperationException(
								$"{method.Name} exited with code {exit.ExitCode}.");
						}
					}
				} catch (OperationCanceledException) when (authenticationCancellation.IsCancellationRequested) {
					return;
				} catch (Exception ex) when (ex is not OperationCanceledException) {
					bool current;
					lock (_turnTransitionGate) {
						lock (_gate) {
							current = ReferenceEquals(_authenticationCancellation, authenticationCancellation)
								&& _authenticationPending;
							if (current) {
								_authenticating = false;
								_authenticationCancellation = null;
							}
						}
						if (current) {
							if (method.Type == "agent" && ex is (IOException or AcpProtocolException)) {
								FailRuntimeSerialized(ex);
							} else EmitFailure(ex);
						}
					}
					return;
				}
				bool requiresUserInput;
				lock (_turnTransitionGate) {
					lock (_gate) {
						if (!ReferenceEquals(_authenticationCancellation, authenticationCancellation)
							|| !_authenticationPending) return;
						_authenticationPending = false;
						_authenticating = false;
						_authenticationOpensSession = false;
						_authenticationCancellation = null;
						_resolvedRequests.Add("authentication");
						requiresUserInput = HasPendingInteractionLocked();
					}
					Observe(new AgentInputResolved(requiresUserInput));
					Emit(new AgentPaneMessage {
						Type = "authentication-resolved",
						ProviderId = _definition.Id,
						ThreadId = SessionId(),
						ItemId = "authentication",
						Status = "accepted",
					});
				}
				if (method.Type == "terminal") {
					Restart(clearSubmissions: false);
				} else if (opensSession) {
					await OpenSessionAsync(generation).ConfigureAwait(false);
				} else {
					FlushPendingSubmissions();
				}
			}
		});
	}

	private AgentLaunch AuthenticationLaunch(AcpAuthMethod method) {
		var invocation = AcpProcessInvocation.Resolve(_definition, _context.Workspace, method.Arguments);
		var environment = new Dictionary<string, string>(_definition.Environment, StringComparer.Ordinal);
		foreach (var entry in method.Environment) environment[entry.Key] = entry.Value;
		return new AgentLaunch {
			Command = invocation.Command,
			Arguments = invocation.Arguments,
			WorkingDirectory = Path.GetFullPath(_context.Workspace),
			RemoveEnvironment = [],
			Environment = environment,
			ExecutableMode = Path.IsPathFullyQualified(invocation.Command)
				? AgentExecutableMode.Direct
				: AgentExecutableMode.SearchPath,
			WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
			OutputCapture = new AgentOutputCapture.Disabled(),
		};
	}

	private void CompleteElicitation(System.Text.Json.JsonElement parameters) =>
		RequiredString(parameters, "elicitationId", "elicitation completion");

	private bool CancelPendingInteractions() {
		bool cancelled = false;
		foreach (var entry in _pendingRequests) {
			if (!_pendingRequests.TryRemove(entry.Key, out var pending)) continue;
			cancelled = true;
			try {
				if (pending.Kind == "permission") {
					CompleteDeferredClientRequest(
						entry.Key,
						new { outcome = new { outcome = "cancelled" } });
				} else {
					CompleteDeferredClientRequest(entry.Key, new { action = "cancel" });
				}
			} catch (Exception ex) when (ex is InvalidOperationException or IOException) {
				// The process generation that owned the request is already gone.
			}
			ResolveInteraction(
				entry.Key,
				pending.Kind == "permission" ? "approval-resolved" : "input-resolved",
				"cancelled",
				pending.Kind == "permission");
		}
		bool cancelAuthentication;
		bool requiresUserInput;
		CancellationTokenSource? authenticationCancellation;
		lock (_gate) {
			cancelAuthentication = _authenticationPending;
			_authenticationPending = false;
			_authenticating = false;
			_authenticationOpensSession = false;
			authenticationCancellation = _authenticationCancellation;
			_authenticationCancellation = null;
			if (cancelAuthentication) _resolvedRequests.Add("authentication");
			requiresUserInput = HasPendingInteractionLocked();
			authenticationCancellation?.Cancel();
		}
		if (!cancelAuthentication) return cancelled;
		Observe(new AgentInputResolved(requiresUserInput));
		Emit(new AgentPaneMessage {
			Type = "authentication-resolved",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			ItemId = "authentication",
			Status = "cancelled",
		});
		return true;
	}

	private void ResolveInteraction(string requestId, string type, string status, bool permission) {
		bool requiresUserInput;
		lock (_gate) {
			_resolvedRequests.Add(requestId);
			requiresUserInput = HasPendingInteractionLocked();
		}
		if (permission) Observe(new AgentPermissionResolved(requiresUserInput));
		else Observe(new AgentInputResolved(requiresUserInput));
		Emit(new AgentPaneMessage {
			Type = type,
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			ItemId = requestId,
			Status = status,
		});
	}

	private void EmitStaleInteraction(string requestId, string kind) {
		lock (_gate) {
			if (_resolvedRequests.Contains(requestId)) return;
		}
		EmitFailure(new AcpProtocolException($"ACP {kind} request '{requestId}' is no longer pending."));
	}

	private static string PermissionStatus(System.Text.Json.JsonElement option) => OptionalString(option, "kind") switch {
		"allow_once" => "allowed once",
		"allow_always" => "always allowed",
		"reject_once" or "reject_always" => "denied",
		_ => OptionalString(option, "name") ?? RequiredString(option, "optionId", "permission option"),
	};

	private bool HasPendingInteractionLocked() => !_pendingRequests.IsEmpty || _authenticationPending;

	private bool CompleteDeferredClientRequest(string requestId, object response) {
		if (!_clientRequests.TryGetValue(requestId, out var state) || !state.TryComplete()) return false;
		_pendingRequests.TryRemove(requestId, out _);
		RespondToCompletedClientRequest(state, response, errorCode: null, errorMessage: null, errorData: null);
		return true;
	}
}
