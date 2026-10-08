using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	internal bool Rewinding {
		get { lock (_gate) return _rewinding; }
		set { lock (_gate) _rewinding = value; }
	}

	/// <summary>Returns the canonical submission, or null when it carries nothing to send.</summary>
	internal AgentTurnSubmission? Normalize(AgentTurnSubmission submission) {
		lock (_gate) {
			submission = NormalizeSubmissionLocked(submission);
			return submission.Text.Length == 0 && submission.Attachments.Count == 0 ? null : submission;
		}
	}

	internal void Enqueue(AgentTurnSubmission submission) {
		lock (_gate) _pendingSubmissions.Enqueue(submission);
	}

	internal void Submit(AgentTurnSubmission submission) {
		if (Normalize(submission) is not { } normalized) return;
		Enqueue(normalized);
		DispatchPendingSubmission();
	}

	private void FlushPendingSubmissions() => DispatchPendingSubmission();

	internal void DispatchPendingSubmission() {
		try {
			lock (_turnTransitionGate) DeliverNextSubmission();
		} finally {
			PublishQueue();
		}
	}

	private void DeliverNextSubmission() {
		AgentTurnSubmission? submission;
		string? threadId;
		bool steer = false;
		lock (_gate) {
			if (!_ready || _rewinding || _authenticationPending || _cancelRequested || _pendingSubmissions.Count == 0
				|| _steering && !_promptActive) {
				return;
			}
			threadId = _sessionId ?? throw new InvalidOperationException("The ACP session is not ready.");
			if (_promptActive) {
				if (!_features.Steering || _steering) return;
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
		}
		if (!steer && TurnId() == "1") RaiseControls(); // The first prompt makes the conversation rewindable.
		var delivery = steer
			? DeliverSteeringAsync(submission)
			: DeliverPromptAsync(threadId, submission);
		Run(() => delivery);
	}

	// Serialized so concurrent publishers cannot deliver an older queue after a newer one and leave the
	// composer showing work that is already on its way to the provider.
	private void PublishQueue() {
		lock (_turnTransitionGate) {
			AgentTurnSubmission[]? waiting = null;
			lock (_gate) {
				if (_pendingSubmissions.Version != _publishedQueueVersion) {
					_publishedQueueVersion = _pendingSubmissions.Version;
					waiting = _pendingSubmissions.Snapshot();
				}
			}
			if (waiting is not null) _port.QueueChanged(waiting);
		}
	}

	private async Task DeliverSteeringAsync(AgentTurnSubmission submission) {
		bool retryAsPrompt = false;
		try {
			Task<JsonElement> request;
			PreparedPrompt prompt;
			lock (_turnTransitionGate) {
				if (!Live) return;
				prompt = BuildPrompt(submission);
				request = _endpoint.Value.RequestAsync(
					"_session/steering",
					new JsonObject {
						["prompt"] = prompt.Blocks,
						["_meta"] = new JsonObject {
							["steering"] = new JsonObject { ["idleBehavior"] = "promptRequired" },
						},
					},
					CancellationToken.None);
			}
			var result = await request.ConfigureAwait(false);
			lock (_turnTransitionGate) {
				if (!Live) return;
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
							$"{Definition.Name} started an untracked turn instead of returning promptRequired.");
					case "failed":
						throw new AcpProtocolException($"{Definition.Name} could not apply the steering prompt.");
					default:
						throw new AcpProtocolException("The ACP steering response has an unknown outcome.");
				}
			}
		} catch (Exception ex) when (ex is not OperationCanceledException) {
			lock (_turnTransitionGate) {
				if (!Live) return;
				if (ex is IOException or AcpProtocolException) FailRuntimeSerialized(ex);
				else EmitFailure(ex);
			}
		} finally {
			bool dispatch;
			lock (_gate) {
				if (Live) _steering = false;
				dispatch = Live && (!retryAsPrompt || !_promptActive);
			}
			if (dispatch) DispatchPendingSubmission();
			else PublishQueue();
		}
	}

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
		if (submission.Kind == AgentTurnSubmissionKind.McpPrompt) {
			var prompt = McpPromptCatalog.Require(submission.CommandName);
			return submission with { Text = CanonicalCommandText(submission.Text, prompt.Name) };
		}
		if (submission.Kind != AgentTurnSubmissionKind.ProviderCommand) {
			throw new InvalidOperationException($"Unknown agent submission kind '{submission.Kind}'.");
		}
		if (submission.Attachments.Count != 0) {
			throw new InvalidOperationException("Provider commands cannot include attachments.");
		}
		if (submission.CommandName.Length == 0) throw new InvalidOperationException("A provider command must include its name.");
		if (_ready) ResolveProviderCommandLocked(submission.CommandName);
		return submission with { Text = CanonicalCommandText(submission.Text, submission.CommandName) };
	}

	private AgentSlashEntry ResolveProviderCommandLocked(string name) {
		if (name.Length == 0) throw new InvalidOperationException("A provider command must include its name.");
		return _commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal))
			?? throw new InvalidOperationException(
				$"{Definition.Name} no longer advertises the '/{name}' command.");
	}

	private static string CanonicalCommandText(string text, string name) {
		string prefix = "/" + name;
		if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			|| text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length])) {
			throw new InvalidOperationException($"The slash command text does not invoke '/{name}'.");
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
				ProviderId = Definition.Id,
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

	internal void Prefill(string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		Emit(new AgentPaneMessage {
			Type = "draft",
			ProviderId = Definition.Id,
			ThreadId = SessionId(),
			Text = prompt,
		});
	}

	internal void Interrupt() {
		lock (_turnTransitionGate) {
			string? sessionId;
			TerminalizedTool[] cancelled = [];
			lock (_gate) {
				if (_spec.SideScoped && !_ready) _pendingSubmissions.Clear();
				_cancelRequested = _promptActive || HasBackgroundWorkLocked();
				sessionId = _ready ? SessionId() : null;
				// ACP: the client marks the cancelled turn's unfinished tool calls cancelled; agents may never report them.
				if (sessionId is not null && _promptActive) {
					string turnId = TurnId();
					cancelled = TerminalizeToolsLocked("cancelled", tool => tool.TurnId == turnId);
				}
			}
			if (sessionId is not null) {
				var cancellation = _endpoint.Value.NotifyAsync("session/cancel", []);
				RunRuntime(() => cancellation);
			}
			ObserveTerminalizedTools(cancelled);
			PublishTerminalizedToolMessages(cancelled);
			PublishQueue();
			bool interactionCancelled = CancelPendingInteractions();
			if (interactionCancelled && sessionId is null && _spec.SideScoped) {
				lock (_gate) if (_sessionOpening) return;
				Terminate(new InvalidOperationException("Side conversation interrupted."));
				return;
			}
			if (interactionCancelled && sessionId is null) {
				Observe(new AgentTurnStopped(WillResume: false));
			}
			if (interactionCancelled) {
				bool settled;
				lock (_gate) {
					settled = _spec.SideScoped
						&& _ready
						&& !_promptActive
						&& !HasBackgroundWorkLocked()
						&& _pendingSubmissions.Count == 0;
				}
				if (settled) SignalSettled();
			}
		}
	}
}
