using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private async Task DeliverPromptAsync(string threadId, AgentTurnSubmission submission) {
		bool guidanceSentBefore = false;
		try {
			Task<JsonElement> request;
			lock (_turnTransitionGate) {
				if (!Live) return;
				lock (_gate) guidanceSentBefore = _guidanceSent;
				var prompt = BuildPrompt(submission);
				try {
					SaveContinuation();
				} catch (AcpSessionStoreException ex) {
					_endpoint.Value.Terminate(ex.Message);
					throw;
				}
				Emit(new AgentPaneMessage {
					Type = "turn-started",
					ProviderId = Definition.Id,
					ThreadId = threadId,
					TurnId = TurnId(),
					IsPrimaryThread = !_spec.Side,
					StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				});
				EmitSubmitted(
					submission,
					submission.Kind == AgentTurnSubmissionKind.ProviderCommand ? "user-command" : "user-message",
					prompt.Images);
				Observe(new AgentPromptSubmitted(threadId, submission.Text));
				request = _endpoint.Value.RequestAsync(
					"session/prompt",
					new JsonObject { ["prompt"] = prompt.Blocks },
					CancellationToken.None);
			}
			var result = await request.ConfigureAwait(false);
			string stopReason = RequiredString(result, "stopReason", "session/prompt response");
			string turnId = TurnId();
			bool background;
			lock (_turnTransitionGate) {
				if (!Live) return;
				lock (_gate) {
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
					ThreadId = threadId,
					TurnId = turnId,
					Status = stopReason,
				});
				if (!background) SignalSettled();
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (!Live) return;
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
						ThreadId = threadId,
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
						ThreadId = threadId,
						TurnId = TurnId(),
						Status = cancelled ? "cancelled" : "failed",
						Summary = ex.Message,
					});
					if (!cancelled) EmitFailure(ex);
					if (!background) SignalSettled();
				}
			}
		} finally {
			bool dispatch = false;
			lock (_turnTransitionGate) {
				if (Live) {
					bool settled;
					lock (_gate) {
						_promptActive = false;
						if (_cancelRequested) _cancelRequested = false;
						settled = ClaimBackgroundSettleLocked();
					}
					if (settled) {
						Observe(new AgentTurnStopped(WillResume: false));
						CompleteContentStreams();
						SignalSettled();
					}
					dispatch = true;
				}
			}
			if (dispatch) DispatchPendingSubmission();
		}
	}
}
