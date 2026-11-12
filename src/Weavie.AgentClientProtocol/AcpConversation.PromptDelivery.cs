using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private async Task DeliverPromptAsync(string threadId, AgentTurnSubmission submission) {
		bool guidanceSentBefore = false;
		JsonElement result = default;
		Exception? failure = null;
		try {
			Task<JsonElement>? request = null;
			lock (_turnTransitionGate) {
				if (Live) {
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
					Background.ClearFinished();
					request = _endpoint.Value.RequestAsync(
						"session/prompt",
						new JsonObject { ["prompt"] = prompt.Blocks },
						CancellationToken.None);
				}
			}
			if (request is not null) result = await request.ConfigureAwait(false);
		} catch (Exception error) {
			failure = error;
		}
		// Completion publication and release of the delivery slot are one transition.
		lock (_turnTransitionGate) {
			try {
				if (!Live) return;
				if (failure is not null) ExceptionDispatchInfo.Throw(failure);
				string stopReason = RequiredString(result, "stopReason", "session/prompt response");
				string turnId = TurnId();
				bool background;
				lock (_gate) {
					_promptActive = false;
					if (_cancelRequested) _cancelRequested = false;
					background = HasBackgroundWorkLocked();
					_waitingForBackground = background;
				}
				CompletePermissionTools(turnId);
				Observe(new AgentTurnStopped(WillResume: background));
				CompleteContentStreams();
				NoticeUnclaimedBlocks();
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
			} catch (Exception ex) when (ex is not OperationCanceledException) {
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
					NoticeUnclaimedBlocks();
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
					NoticeUnclaimedBlocks();
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
			} finally {
				lock (_gate) _promptDeliveryActive = false;
				if (Live) DispatchPendingSubmission();
			}
		}
	}
}
