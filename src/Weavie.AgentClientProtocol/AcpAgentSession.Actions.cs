using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);

		lock (_turnTransitionGate) {
			bool reconnect;
			lock (_gate) {
				ObjectDisposedException.ThrowIf(_disposed, this);
				submission = NormalizeSubmissionLocked(submission);
				if (submission.Text.Length == 0 && submission.Attachments.Count == 0) return;
				reconnect = _runtimeFailed;
				if (reconnect && _sessionId is not null && !_supportsLoad && !_supportsResume) {
					throw new InvalidOperationException(
						$"{_definition.Name} cannot restore this conversation. Start a new conversation to continue.");
				}
				_pendingSubmissions.Enqueue(submission);
			}
			if (reconnect) Restart(clearSubmissions: false);
		}
		DispatchPendingSubmission();
	}

	private void FlushPendingSubmissions() => DispatchPendingSubmission();

	private void DispatchPendingSubmission() {
		try {
			lock (_turnTransitionGate) DeliverNextSubmission();
		} finally {
			PublishQueue();
		}
	}

	private void DeliverNextSubmission() {
		AgentTurnSubmission? submission;
		string? sessionId;
		bool steer = false;
		long epoch;
		lock (_gate) {
			if (!_ready || _authenticationPending || _cancelRequested || _pendingSubmissions.Count == 0
				|| _steering && !_promptActive) {
				return;
			}
			sessionId = _sessionId ?? throw new InvalidOperationException("The ACP session is not ready.");
			if (_promptActive) {
				if (!_supportsSteering || _steering) return;
				// A provider command owns its own turn, so it waits here without holding back what steers past it.
				submission = _pendingSubmissions.TakeFirst(pending => pending.Kind == AgentTurnSubmissionKind.Prompt);
				if (submission is null) return;
				steer = true;
				_steering = true;
			} else {
				submission = _pendingSubmissions.Dequeue();
				_promptActive = true;
				_waitingForBackground = false;
				_turnNumber++;
			}
			epoch = _submissionEpoch;
		}
		var delivery = steer
			? DeliverSteeringAsync(submission, epoch)
			: DeliverPromptAsync(sessionId, submission, epoch);
		Run(() => delivery);
	}

	// Serialized so concurrent publishers cannot deliver an older queue after a newer one and leave the
	// composer showing work that is already on its way to the provider.
	private void PublishQueue() {
		lock (_queuePublishGate) {
			AgentTurnSubmission[]? waiting = null;
			lock (_gate) {
				if (_pendingSubmissions.Version != _publishedQueueVersion) {
					_publishedQueueVersion = _pendingSubmissions.Version;
					waiting = _pendingSubmissions.Snapshot();
				}
			}
			if (waiting is not null) QueuedSubmissionsChanged?.Invoke(waiting);
		}
	}

	private async Task DeliverSteeringAsync(AgentTurnSubmission submission, long epoch) {
		bool retryAsPrompt = false;
		long generation = 0;
		try {
			Task<JsonElement> request;
			PreparedPrompt prompt;
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (epoch != _submissionEpoch) return;
					generation = _activeGeneration;
				}
				prompt = BuildPrompt(submission);
				request = Endpoint(generation).RequestAsync(
					"_session/steering",
					new {
						prompt = prompt.Blocks,
						_meta = new { steering = new { idleBehavior = "promptRequired" } },
					},
					CancellationToken.None);
			}
			var result = await request.ConfigureAwait(false);
			lock (_turnTransitionGate) {
				if (!OwnsOperation(generation, epoch)) return;
				switch (RequiredString(result, "outcome", "_session/steering response")) {
					case "injected":
						EmitSubmitted(submission, "user-steer", prompt.Images);
						break;
					case "promptRequired":
						lock (_gate) _pendingSubmissions.Requeue(submission);
						retryAsPrompt = true;
						break;
					case "startedNewTurn":
						throw new AcpProtocolException(
							$"{_definition.Name} started an untracked turn instead of returning promptRequired.");
					case "failed":
						throw new AcpProtocolException($"{_definition.Name} could not apply the steering prompt.");
					default:
						throw new AcpProtocolException("The ACP steering response has an unknown outcome.");
				}
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (!OwnsOperation(generation, epoch)) return;
				if (ex is IOException or AcpProtocolException) FailRuntimeSerialized(ex);
				else EmitFailure(ex);
			}
		} finally {
			bool dispatch;
			lock (_gate) {
				if (epoch == _submissionEpoch) _steering = false;
				dispatch = epoch == _submissionEpoch && (!retryAsPrompt || !_promptActive);
			}
			if (dispatch) DispatchPendingSubmission();
			else PublishQueue();
		}
	}

	private async Task DeliverPromptAsync(string sessionId, AgentTurnSubmission submission, long epoch) {
		long generation = 0;
		bool guidanceSentBefore = false;
		try {
			lock (_gate) {
				if (epoch != _submissionEpoch) return;
			}
			Task<JsonElement> request;
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (epoch != _submissionEpoch) return;
					generation = _activeGeneration;
					guidanceSentBefore = _guidanceSent;
				}
				var prompt = BuildPrompt(submission);
				try {
					SaveContinuation();
				} catch (AcpSessionStoreException ex) {
					_connection.TerminateGeneration(generation, ex.Message);
					throw;
				}
				Emit(new AgentPaneMessage {
					Type = "turn-started",
					ProviderId = _definition.Id,
					ThreadId = sessionId,
					TurnId = TurnId(),
					IsPrimaryThread = _role is PrimaryRole,
					StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				});
				EmitSubmitted(
					submission,
					submission.Kind == AgentTurnSubmissionKind.ProviderCommand ? "user-command" : "user-message",
					prompt.Images);
				Observe(new AgentPromptSubmitted(sessionId, submission.Text));
				request = Endpoint(generation).RequestAsync(
					"session/prompt",
					new { prompt = prompt.Blocks },
					CancellationToken.None);
			}
			var result = await request.ConfigureAwait(false);
			string stopReason = RequiredString(result, "stopReason", "session/prompt response");
			string turnId = TurnId();
			bool background;
			lock (_turnTransitionGate) {
				lock (_gate) {
					if (_disposed || _activeGeneration != generation) return;
					_promptActive = false;
					if (_cancelRequested) _cancelRequested = false;
					background = HasBackgroundWorkLocked();
					_waitingForBackground = background;
				}
				CompletePermissionTools(turnId);
				Observe(new AgentTurnStopped(WillResume: background));
				CompleteContentStreams();
				if (stopReason == "refusal") RetractTurn(turnId);
				else ForgetTurnItems(turnId);
				Emit(new AgentPaneMessage {
					Type = "turn-completed",
					ProviderId = _definition.Id,
					ThreadId = sessionId,
					TurnId = turnId,
					Status = stopReason,
				});
				if (!background) SignalSideTurnSettled();
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (!OwnsOperation(generation, epoch)) return;
				bool cancellationFailed;
				lock (_gate) cancellationFailed = _cancelRequested && ex is not AcpRequestException { Code: -32800 };
				if (cancellationFailed) {
					FailRuntimeSerialized(new AcpProtocolException($"{_definition.Name} could not cancel the turn: {ex.Message}"));
				} else if (ex is AcpRequestException { Code: -32000 } authenticationRequired) {
					TerminalizedTool[] tools;
					string turnId = TurnId();
					lock (_gate) {
						tools = TerminalizeActiveToolsLocked("failed");
						_promptActive = false;
						_waitingForBackground = false;
						_guidanceSent = guidanceSentBefore;
						_pendingSubmissions.Requeue(submission);
					}
					ObserveTerminalizedTools(tools);
					Observe(new AgentTurnStopped(WillResume: false));
					CompleteContentStreams();
					PublishTerminalizedToolMessages(tools);
					RetractTurn(turnId);
					Emit(new AgentPaneMessage {
						Type = "turn-completed",
						ProviderId = _definition.Id,
						ThreadId = sessionId,
						TurnId = turnId,
						Status = "authentication_required",
					});
					RequestAuthentication(authenticationRequired.Message, opensSession: false);
				} else if (ex is IOException or AcpProtocolException) {
					FailRuntimeSerialized(ex);
				} else {
					bool cancelled = ex is AcpRequestException { Code: -32800 };
					TerminalizedTool[] tools;
					bool background;
					lock (_gate) {
						tools = TerminalizeActiveToolsLocked(cancelled ? "cancelled" : "failed");
						_promptActive = false;
						if (_cancelRequested) _cancelRequested = false;
						background = HasBackgroundWorkLocked();
						_waitingForBackground = background;
					}
					ObserveTerminalizedTools(tools);
					Observe(new AgentTurnStopped(WillResume: background));
					CompleteContentStreams();
					PublishTerminalizedToolMessages(tools);
					Emit(new AgentPaneMessage {
						Type = "turn-completed",
						ProviderId = _definition.Id,
						ThreadId = sessionId,
						TurnId = TurnId(),
						Status = cancelled ? "cancelled" : "failed",
						Summary = ex.Message,
					});
					if (!cancelled) EmitFailure(ex);
					if (!background) SignalSideTurnSettled();
				}
			}
		} finally {
			bool dispatch = false;
			lock (_turnTransitionGate) {
				if (OwnsOperation(generation, epoch)) {
					bool settled;
					lock (_gate) {
						_promptActive = false;
						if (_cancelRequested) _cancelRequested = false;
						settled = ClaimBackgroundSettleLocked();
					}
					if (settled) {
						Observe(new AgentTurnStopped(WillResume: false));
						CompleteContentStreams();
						SignalSideTurnSettled();
					}
					dispatch = true;
				}
			}
			if (dispatch) DispatchPendingSubmission();
		}
	}


	private bool OwnsOperation(long generation, long epoch) {
		lock (_gate) return OwnsOperationLocked(generation, epoch);
	}

	private bool OwnsOperationLocked(long generation, long epoch) =>
		!_disposed && _activeGeneration == generation && _submissionEpoch == epoch;

	private bool ClaimBackgroundSettleLocked() {
		if (_promptActive || HasBackgroundWorkLocked() || !_waitingForBackground) {
			return false;
		}
		_waitingForBackground = false;
		return true;
	}

	private AgentTurnSubmission NormalizeSubmissionLocked(AgentTurnSubmission submission) {
		if (submission.Kind == AgentTurnSubmissionKind.Prompt) {
			if (submission.CommandName.Length != 0) {
				throw new InvalidOperationException("An ordinary prompt cannot name a provider command.");
			}
			return submission;
		}
		if (submission.Kind != AgentTurnSubmissionKind.ProviderCommand) {
			throw new InvalidOperationException($"Unknown agent submission kind '{submission.Kind}'.");
		}
		if (submission.Attachments.Count != 0) {
			throw new InvalidOperationException("Provider commands cannot include attachments.");
		}
		var command = ResolveProviderCommandLocked(submission.CommandName);
		return submission with { Text = CanonicalCommandText(submission.Text, command) };
	}

	private AgentSlashEntry ResolveProviderCommandLocked(string name) {
		if (name.Length == 0) throw new InvalidOperationException("A provider command must include its name.");
		return _commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal))
			?? throw new InvalidOperationException(
				$"{_definition.Name} no longer advertises the '/{name}' command.");
	}

	private static string CanonicalCommandText(string text, AgentSlashEntry command) {
		string prefix = "/" + command.Name;
		if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			|| text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length])) {
			throw new InvalidOperationException($"The provider command text does not invoke '/{command.Name}'.");
		}
		return prefix + text[prefix.Length..];
	}

	private void RetractTurn(string turnId) {
		string[] itemIds;
		lock (_gate) {
			itemIds = _turnItemIds.Remove(turnId, out var items) ? [.. items] : [];
		}
		foreach (string itemId in itemIds) {
			Emit(new AgentPaneMessage {
				Type = "item-retracted",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = turnId,
				ItemId = itemId,
				Status = "retracted",
			});
		}
	}

	private void ForgetTurnItems(string turnId) {
		lock (_gate) _turnItemIds.Remove(turnId);
	}

	/// <inheritdoc/>
	public void PrefillPrompt(string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		Emit(new AgentPaneMessage {
			Type = "draft",
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			Text = prompt,
		});
	}

	/// <inheritdoc/>
	public void Interrupt() {
		lock (_turnTransitionGate) {
			SideRuntime[] activeSides;
			lock (_gate) {
				activeSides = !_promptActive && !HasBackgroundWorkLocked() && !HasPendingInteractionLocked()
					? [.. _sideRuntimes.Values.Where(side => side.Session.HasWork())]
					: [];
			}
			if (activeSides.Length > 0) {
				foreach (var side in activeSides) side.Session.Interrupt();
				return;
			}
			string? sessionId;
			lock (_gate) {
				if (_role is SideRole && !_ready) _pendingSubmissions.Clear();
				_cancelRequested = _promptActive || HasBackgroundWorkLocked();
				sessionId = _ready ? SessionId() : null;
			}
			if (sessionId is not null) {
				long generation;
				lock (_gate) generation = _activeGeneration;
				var cancellation = Endpoint(generation).NotifyAsync("session/cancel", new { });
				RunRuntime(generation, () => cancellation);
			}
			PublishQueue();
			bool interactionCancelled = CancelPendingInteractions();
			if (interactionCancelled && sessionId is null && _role is SideRole) {
				lock (_gate) if (_sessionOpening) return;
				FailConversationSerialized(new InvalidOperationException("Side conversation interrupted."));
				return;
			}
			if (interactionCancelled && sessionId is null) {
				Observe(new AgentTurnStopped(WillResume: false));
			}
			if (interactionCancelled) {
				bool settled;
				lock (_gate) {
					settled = _role is SideRole
						&& _ready
						&& !_promptActive
						&& !HasBackgroundWorkLocked()
						&& _pendingSubmissions.Count == 0;
				}
				if (settled) SignalSideTurnSettled();
			}
		}
	}

	/// <inheritdoc/>
	public void Restart() {
		bool clearSubmissions;
		lock (_gate) clearSubmissions = !_runtimeFailed;
		Restart(clearSubmissions);
	}

	private void Restart(bool clearSubmissions) {
		lock (_turnTransitionGate) {
			if (_role is SideRole) throw new InvalidOperationException("Restart the owning primary conversation.");
			if (!_displayRestored) RestoreDisplay();
			else if (_storageFailed) SaveContinuation();
			_storageFailed = false;
			TerminalizeForRestart(clearSubmissions, "ACP agent restarted.");
			SuspendSideRuntimes("ACP agent restarted.");
			_connection.Restart();
		}
	}

	/// <inheritdoc/>
	public void StartNewConversation() {
		if (_role is not PrimaryRole) {
			throw new InvalidOperationException("Only the primary ACP conversation can be replaced.");
		}
		SideRuntime[] sideSessions;
		lock (_turnTransitionGate) {
			TerminalizeForRestart(clearSubmissions: true, "Started a fresh conversation.");
			lock (_gate) sideSessions = [.. _sideRuntimes.Values];
			foreach (var side in sideSessions) side.Session.TerminalizeForRestart(clearSubmissions: true, "Started a fresh conversation.");
			lock (_gate) {
				_sessionId = null;
				_turnNumber = 0;
				_guidanceSent = false;
				_planTurns.Clear();
				_sideRuntimes.Clear();
			}
			_sideConversations.Clear();
			_sessions.Clear(_definition.Id, _context.Workspace);
			_storageFailed = false;
			_displayRestored = true;
			Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = _definition.Id });
			_connection.Restart();
		}
		foreach (var side in sideSessions) DisposeSideRuntime(side);
	}

	private void TerminalizeForRestart(bool clearSubmissions, string summary) {
		TerminalizedTool[] tools;
		bool promptActive;
		long generation;
		lock (_gate) {
			generation = _activeGeneration;
			_activeGeneration = 0;
			_ready = false;
			if (clearSubmissions) _pendingSubmissions.Clear();
			_submissionEpoch++;
			_cancelRequested = false;
			promptActive = _promptActive;
			_promptActive = false;
			_steering = false;
			_waitingForBackground = false;
			tools = TerminalizeActiveToolsLocked("cancelled");
		}
		if (generation > 0) _terminals.ReleaseGeneration(generation);
		PublishQueue();
		ObserveTerminalizedTools(tools);
		if (promptActive || tools.Length > 0) {
			Observe(new AgentTurnStopped(WillResume: false));
		}
		CompleteContentStreams();
		PublishTerminalizedToolMessages(tools);
		if (promptActive) {
			Emit(new AgentPaneMessage {
				Type = "turn-completed",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				Status = "cancelled",
				Summary = summary,
			});
		}
	}

}
