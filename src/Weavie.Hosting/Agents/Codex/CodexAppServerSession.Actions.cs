using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Handles user actions sent to a native Codex app-server session.</summary>
public sealed partial class CodexAppServerSession {
	/// <inheritdoc/>
	public void Submit(AgentTurnSubmission submission) {
		ArgumentNullException.ThrowIfNull(submission);
		var input = new CodexTurnInput(submission.Text, submission.Attachments, submission.Skills);
		if (input.Text.Length == 0 && input.Images.Count == 0 && input.SkillNames.Count == 0) {
			return;
		}

		SubmitTurn(input);
	}

	private void SubmitTurn(CodexTurnInput input) {
		Run(async () => {
			// Check-and-enqueue under one lock hold: AdoptThread + FlushPendingInputs take the same gate, so an
			// input can no longer slip between a null thread-id read and the enqueue and strand in the queue.
			string? threadId;
			lock (_gate) {
				threadId = _threadId;
				if (string.IsNullOrEmpty(threadId)) {
					_pendingInputs.Enqueue(input);
				}
			}
			if (string.IsNullOrEmpty(threadId)) {
				return;
			}

			// Resolve staged skills at send time (they load asynchronously and can change via skills/changed).
			var skills = ResolveSkills(input.SkillNames);
			if (input.Text.Length == 0 && input.Images.Count == 0 && skills.Count == 0) {
				EmitError(input.SkillNames.Count > 0
					? "That skill is no longer available; nothing was sent."
					: "Write a prompt, attach an image, or add a skill before running Codex.");
				return;
			}

			await DeliverAsync(threadId, input, skills).ConfigureAwait(false);
		});
	}

	// Deliver to Codex's current turn; a stale-turn rejection means our view lagged, so re-read and steer the
	// turn it moved to. _turnId only ever advances or clears, so this converges on a live steer or a fresh start.
	private async Task DeliverAsync(string threadId, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		while (CurrentTurnId() is { Length: > 0 } turnId) {
			if (await TrySteerAsync(threadId, turnId, input, skills).ConfigureAwait(false)) {
				return;
			}

			DiscardStaleTurn(turnId);
		}

		await StartTurnAsync(threadId, input, skills).ConfigureAwait(false);
	}

	private async Task<bool> TrySteerAsync(string threadId, string turnId, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		long id = NextRequest();
		try {
			await _client.RequestAsync(id, RequestFor(id, threadId, turnId, input, skills), CancellationToken.None).ConfigureAwait(false);
		} catch (CodexRequestException ex) when (IsStaleTurnRejection(ex)) {
			// The turn ended under us. Keep the protocol detail (code + raw envelope) visible, then recover.
			Emit(new AgentPaneMessage {
				Type = "warning",
				ProviderId = "codex",
				ThreadId = threadId,
				Summary = "Steer rejected; resent as a new turn",
				Text = ex.Detail,
				Status = "warning",
				PayloadJson = ex.Payload,
			});
			return false;
		}

		EmitSubmittedInput(threadId, turnId, starting: false, input, skills);
		return true;
	}

	private async Task StartTurnAsync(string threadId, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		long id = NextRequest();
		await _client.RequestAsync(id, RequestFor(id, threadId, null, input, skills), CancellationToken.None).ConfigureAwait(false);
		EmitSubmittedInput(threadId, null, starting: true, input, skills);
	}

	// Forget the turn we just failed to steer, unless Codex has already started a newer one under us.
	private void DiscardStaleTurn(string turnId) {
		lock (_gate) {
			if (string.Equals(_turnId, turnId, StringComparison.Ordinal)) {
				_turnId = null;
			}
		}
	}

	private static bool IsStaleTurnRejection(CodexRequestException ex) =>
		ex.Code == -32600 && ex.Message.Contains("active turn", StringComparison.OrdinalIgnoreCase);

	private string RequestFor(long id, string threadId, string? turnId, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		if (input.Images.Count > 0 || skills.Count > 0) {
			string[] imagePaths = [.. input.Images.Select(image => image.Path)];
			return string.IsNullOrEmpty(turnId)
				? CodexAppServerProtocol.TurnStartWithInputs(id, threadId, input.Text, imagePaths, skills, _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), EffectiveModel(), EffectiveEffort(), EffectiveServiceTier())
				: CodexAppServerProtocol.TurnSteerWithInputs(id, threadId, turnId, input.Text, imagePaths, skills);
		}

		return string.IsNullOrEmpty(turnId)
			? CodexAppServerProtocol.TurnStart(id, threadId, input.Text, _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), EffectiveModel(), EffectiveEffort(), EffectiveServiceTier())
			: CodexAppServerProtocol.TurnSteer(id, threadId, turnId, input.Text);
	}

	private void EmitSubmittedInput(string threadId, string? turnId, bool starting, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		string text = input.Text;
		if (skills.Count > 0) {
			string invoked = "↪ skill: " + string.Join(", ", skills.Select(skill => skill.Name));
			text = text.Length > 0 ? $"{text}\n{invoked}" : invoked;
		}

		if (text.Length > 0) {
			Emit(new AgentPaneMessage {
				Type = starting ? "user-message" : "user-steer",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				Text = text,
			});
		}

		foreach (var image in input.Images) {
			Emit(new AgentPaneMessage {
				Type = "user-image",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				ItemId = image.Id,
				Text = image.Path,
				Status = "submitted",
			});
		}
	}

	/// <inheritdoc/>
	public void PrefillPrompt(string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		Emit(new AgentPaneMessage {
			Type = "draft",
			ProviderId = "codex",
			ThreadId = CurrentThreadId(),
			Text = prompt,
		});
	}

	/// <inheritdoc/>
	public void Interrupt() {
		(string? threadId, string? turnId) = CurrentTurn();
		if (string.IsNullOrEmpty(threadId) || string.IsNullOrEmpty(turnId)) {
			return;
		}

		Run(async () => {
			long id = NextRequest();
			await _client.RequestAsync(id, CodexAppServerProtocol.TurnInterrupt(id, threadId, turnId), CancellationToken.None)
				.ConfigureAwait(false);
			Emit(new AgentPaneMessage {
				Type = "interrupted",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				Status = "interrupted",
			});
		});
	}

	/// <inheritdoc/>
	public void Restart() => _client.Restart();

	/// <inheritdoc/>
	public void ResolveApproval(string requestId, string decision) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(decision);
		if (!_pendingRequests.TryGetValue(requestId, out var request)) {
			// Re-answering an already-answered card is a benign double-click; an id we never knew (or that a
			// restart voided) must unwedge the card and explain itself.
			if (!_resolvedRequests.ContainsKey(requestId)) {
				EmitStaleRequest(requestId, "approval-resolved", "approval");
			}

			return;
		}

		if (!CodexApprovalResponses.CanResolve(request.Method)) {
			EmitError($"Codex request {requestId} is not an approval request.");
			return;
		}

		try {
			_client.Respond(request.ResponseId, CodexApprovalResponses.Build(request, decision));
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or KeyNotFoundException) {
			EmitError(ex.Message);
			return;
		}

		_pendingRequests.TryRemove(requestId, out _);
		_resolvedRequests[requestId] = 0;
		_context.Events.Observe(new AgentPermissionResolved(HasPendingUserRequest()));
		Emit(new AgentPaneMessage {
			Type = "approval-resolved",
			ProviderId = "codex",
			Status = decision,
			ItemId = requestId,
		});
	}

	/// <inheritdoc/>
	public void ResolveInput(string requestId, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentNullException.ThrowIfNull(answers);
		if (!_pendingRequests.TryGetValue(requestId, out var request)) {
			if (!_resolvedRequests.ContainsKey(requestId)) {
				EmitStaleRequest(requestId, "input-resolved", "answer");
			}

			return;
		}

		if (!CodexInputResponses.CanResolve(request.Method)) {
			EmitError($"Codex request {requestId} is not a user-input request.");
			return;
		}

		try {
			_client.Respond(request.ResponseId, CodexInputResponses.Build(answers));
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) {
			EmitError(ex.Message);
			return;
		}

		_pendingRequests.TryRemove(requestId, out _);
		_resolvedRequests[requestId] = 0;
		_context.Events.Observe(new AgentPermissionResolved(HasPendingUserRequest()));
		Emit(new AgentPaneMessage {
			Type = "input-resolved",
			ProviderId = "codex",
			Status = "resolved",
			ItemId = requestId,
		});
	}

	private void EmitError(string text) =>
		Emit(new AgentPaneMessage {
			Type = "error",
			ProviderId = "codex",
			Text = text,
			Status = "error",
		});

	private bool HasPendingUserRequest() {
		foreach (var request in _pendingRequests.Values) {
			if (CodexApprovalResponses.CanResolve(request.Method) || CodexInputResponses.CanResolve(request.Method)) {
				return true;
			}
		}

		return false;
	}

	// A decision can race a restart that voided the request: unwedge the card and say why — never eat the click.
	private void EmitStaleRequest(string requestId, string resolutionType, string kind) {
		Emit(new AgentPaneMessage {
			Type = resolutionType,
			ProviderId = "codex",
			Status = "cancel",
			ItemId = requestId,
		});
		EmitError($"Codex is no longer waiting on this {kind} (usually because the app-server restarted); nothing was sent.");
		_context.Events.Observe(new AgentPermissionResolved(HasPendingUserRequest()));
	}
}
