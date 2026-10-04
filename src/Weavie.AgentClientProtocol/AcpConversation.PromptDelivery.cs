using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
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
					ProviderId = Definition.Id,
					ThreadId = sessionId,
					TurnId = TurnId(),
					IsPrimaryThread = !_spec.SideScoped,
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
					ProviderId = Definition.Id,
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
					FailRuntimeSerialized(new AcpProtocolException($"{Definition.Name} could not cancel the turn: {ex.Message}"));
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
						ProviderId = Definition.Id,
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
						ProviderId = Definition.Id,
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
}
