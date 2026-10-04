using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
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

	internal void Authenticate(string requestId, string methodId) {
		var method = _features.AuthMethods.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, methodId, StringComparison.Ordinal));
		if (method is null) {
			EmitFailure(new AcpProtocolException($"'{methodId}' is not an advertised ACP authentication method."));
			return;
		}
		bool authenticate;
		bool opensSession;
		CancellationTokenSource? cancellation = null;
		lock (_gate) {
			authenticate = _authenticationPending
				&& !_authenticating
				&& string.Equals(_authenticationItemId, requestId, StringComparison.Ordinal);
			opensSession = _authenticationOpensSession;
			if (authenticate) {
				_authenticating = true;
				cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
				_authenticationCancellation = cancellation;
			}
		}
		if (!authenticate) {
			EmitStaleInteraction(requestId, "authentication");
			return;
		}
		var authenticationCancellation = cancellation!;
		Run(async () => {
			using (authenticationCancellation) {
				try {
					if (method.Type == "agent") {
						await _endpoint.Value.AuthenticateAsync(
							methodId,
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
				string authenticationItemId;
				lock (_turnTransitionGate) {
					lock (_gate) {
						if (!ReferenceEquals(_authenticationCancellation, authenticationCancellation)
							|| !_authenticationPending) return;
						_authenticationPending = false;
						_authenticating = false;
						_authenticationOpensSession = false;
						_authenticationCancellation = null;
						authenticationItemId = _authenticationItemId
							?? throw new AcpProtocolException("The ACP authentication item identity is missing.");
						_authenticationItemId = null;
						_resolvedRequests.Add(authenticationItemId);
						requiresUserInput = HasPendingInteractionLocked();
					}
					Observe(new AgentInputResolved(requiresUserInput));
					Emit(new AgentPaneMessage {
						Type = "authentication-resolved",
						ProviderId = Definition.Id,
						ThreadId = SessionId(),
						ItemId = authenticationItemId,
						RequestId = authenticationItemId,
						Status = "accepted",
					});
				}
				if (method.Type == "terminal") {
					_port.RestartProcess();
				} else if (opensSession) {
					await OpenSessionAsync().ConfigureAwait(false);
				} else {
					FlushPendingSubmissions();
				}
			}
		});
	}

	private AgentLaunch AuthenticationLaunch(AcpAuthMethod method) {
		var environment = new Dictionary<string, string>(Definition.Environment, StringComparer.Ordinal);
		foreach (var entry in method.Environment) environment[entry.Key] = entry.Value;
		return new AgentLaunch {
			Command = Definition.Command,
			Arguments = [.. Definition.Arguments, .. method.Arguments],
			WorkingDirectory = Path.GetFullPath(_context.Workspace),
			RemoveEnvironment = [],
			Environment = environment,
			ExecutableMode = Path.IsPathFullyQualified(Definition.Command)
				? AgentExecutableMode.Direct
				: AgentExecutableMode.SearchPath,
			WorkingDirectoryMode = AgentWorkingDirectoryMode.Fixed,
			OutputCapture = new AgentOutputCapture.Disabled(),
		};
	}
}
