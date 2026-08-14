using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	/// <inheritdoc/>
	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		if (submission.Text.Length == 0 && submission.Attachments.Count == 0) {
			return;
		}

		lock (_gate) {
			_pendingSubmissions.AddLast(submission);
		}
		DispatchPendingSubmission();
	}

	private void FlushPendingSubmissions() => DispatchPendingSubmission();

	private void DispatchPendingSubmission() {
		AgentTurnSubmission? submission = null;
		string? sessionId = null;
		bool steer = false;
		long epoch = 0;
		lock (_gate) {
			if (!_ready || _authenticationPending || _cancelRequested || _pendingSubmissions.Count == 0
				|| _steering && !_promptActive) {
				return;
			}
			sessionId = _sessionId ?? throw new InvalidOperationException("The ACP session is not ready.");
			if (_promptActive) {
				if (!_supportsSteering || _steering) return;
				steer = true;
				_steering = true;
			} else {
				_promptActive = true;
				_waitingForBackground = false;
				_turnNumber++;
			}
			submission = _pendingSubmissions.First!.Value;
			_pendingSubmissions.RemoveFirst();
			epoch = _submissionEpoch;
		}
		Run(steer
			? () => DeliverSteeringAsync(sessionId, submission, epoch)
			: () => DeliverPromptAsync(sessionId, submission, epoch));
	}

	private async Task DeliverSteeringAsync(string sessionId, AgentTurnSubmission submission, long epoch) {
		bool retryAsPrompt = false;
		long generation = 0;
		try {
			Task<JsonElement> request;
			lock (_submissionDispatchGate) {
				lock (_gate) {
					if (epoch != _submissionEpoch) return;
					generation = _activeGeneration;
				}
				object[] prompt = BuildPrompt(submission);
				request = _connection.RequestAsync(
					"_session/steering",
					new {
						sessionId,
						prompt,
						_meta = new { steering = new { idleBehavior = "promptRequired" } },
					},
					CancellationToken.None);
			}
			var result = await request.ConfigureAwait(false);
			lock (_turnTransitionGate) {
				if (!OwnsOperation(generation, epoch)) return;
				switch (RequiredString(result, "outcome", "_session/steering response")) {
					case "injected":
						EmitSubmitted(submission, "user-steer");
						break;
					case "promptRequired":
						lock (_gate) _pendingSubmissions.AddFirst(submission);
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
			lock (_submissionDispatchGate) {
				lock (_gate) {
					if (epoch != _submissionEpoch) return;
					generation = _activeGeneration;
					guidanceSentBefore = _guidanceSent;
				}
				try {
					_sessions.Adopt(_definition.Id, _context.Workspace, sessionId, _turnNumber);
				} catch (AcpSessionStoreException ex) {
					_connection.TerminateGeneration(generation, ex.Message);
					throw;
				}
				object[] prompt = BuildPrompt(submission);
				Emit(new AgentPaneMessage {
					Type = "turn-started",
					ProviderId = _definition.Id,
					ThreadId = sessionId,
					TurnId = TurnId(),
					IsPrimaryThread = true,
					StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				});
				EmitSubmitted(submission, "user-message");
				Observe(new AgentPromptSubmitted(sessionId, submission.Text));
				request = _connection.RequestAsync(
					"session/prompt",
					new { sessionId, prompt },
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
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (!OwnsOperation(generation, epoch)) return;
				if (ex is AcpRequestException { Code: -32000 } authenticationRequired) {
					TerminalizedTool[] tools;
					string turnId = TurnId();
					lock (_gate) {
						tools = TerminalizeActiveToolsLocked("failed");
						_promptActive = false;
						_waitingForBackground = false;
						_guidanceSent = guidanceSentBefore;
						_pendingSubmissions.AddFirst(submission);
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

	private object[] BuildPrompt(AgentTurnSubmission submission) {
		var blocks = new List<object>();
		bool includesGuidance = false;
		if (submission.Text.Length > 0) {
			blocks.Add(new { type = "text", text = submission.Text });
		}
		foreach (var attachment in submission.Attachments) {
			if (!_supportsImages) {
				throw new AcpProtocolException($"{_definition.Name} does not accept image prompts.");
			}
			blocks.Add(new {
				type = "image",
				mimeType = attachment.Mime,
				data = Convert.ToBase64String(_context.FileSystem.ReadAllBytes(attachment.Path)),
			});
		}

		if (_supportsEmbeddedContext) {
			lock (_gate) includesGuidance = !_guidanceSent;
			if (includesGuidance) {
				blocks.Add(TextResource(
					"weavie://instructions",
					EmbeddedAgentGuidance.Compose(_context.Runtime)));
			}
			if (_context.Editor.Active is { } editor) {
				string selection = $"Active file: {editor.FilePath}\n"
					+ $"Language: {editor.LanguageId ?? "unknown"}\n"
					+ $"Selection: {editor.Selection.Start.Line + 1}:{editor.Selection.Start.Character + 1}"
					+ $"-{editor.Selection.End.Line + 1}:{editor.Selection.End.Character + 1}\n"
					+ editor.SelectedText;
				string path = Path.GetFullPath(editor.FilePath);
				string uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri;
				blocks.Add(TextResource(uri + "#selection", selection));
			}
		}
		if (includesGuidance) {
			lock (_gate) _guidanceSent = true;
		}

		return [.. blocks];
	}

	private static object TextResource(string uri, string text) => new {
		type = "resource",
		resource = new {
			uri,
			mimeType = "text/plain",
			text,
		},
	};

	private void EmitSubmitted(AgentTurnSubmission submission, string type) {
		if (submission.Text.Length > 0) {
			Emit(new AgentPaneMessage {
				Type = type,
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = submission.Id,
				Text = submission.Text,
			});
		}
		foreach (var attachment in submission.Attachments) {
			Emit(new AgentPaneMessage {
				Type = "user-image",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = attachment.Id,
				Text = attachment.Path,
				Status = "submitted",
			});
		}
	}

	private void RetractTurn(string turnId) {
		string[] itemIds;
		lock (_gate) {
			itemIds = _turnItemIds.Remove(turnId, out var items) ? [.. items] : [];
		}
		foreach (string itemId in itemIds) {
			PaneMessage?.Invoke(new AgentPaneMessage {
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
		string? sessionId;
		lock (_submissionDispatchGate) {
			lock (_gate) {
				_pendingSubmissions.Clear();
				_submissionEpoch++;
				_cancelRequested = _promptActive || HasBackgroundWorkLocked();
				sessionId = _sessionId ?? _openingSessionId;
			}
			if (sessionId is not null) {
				long generation;
				lock (_gate) generation = _activeGeneration;
				RunRuntime(generation, () => _connection.NotifyAsync("session/cancel", new { sessionId }));
			}
		}
		if (CancelPendingInteractions() && sessionId is null) {
			Observe(new AgentTurnStopped(WillResume: false));
		}
	}

	/// <inheritdoc/>
	public void Restart() => Restart(clearSubmissions: true);

	private void Restart(bool clearSubmissions) {
		lock (_submissionDispatchGate) {
			lock (_turnTransitionGate) {
				TerminalizeForRestart(clearSubmissions);
				_connection.Restart();
			}
		}
	}

	private void TerminalizeForRestart(bool clearSubmissions) {
		TerminalizedTool[] tools;
		bool promptActive;
		lock (_gate) {
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
				Summary = "ACP agent restarted.",
			});
		}
	}

}
