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
			(string? threadId, string? turnId) = CurrentTurn();
			if (string.IsNullOrEmpty(threadId)) {
				lock (_gate) {
					_pendingInputs.Enqueue(input);
				}
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

			long id = NextRequest();
			bool starting = string.IsNullOrEmpty(turnId);
			string request = RequestFor(id, threadId, turnId, input, skills);
			await _client.RequestAsync(id, request, CancellationToken.None).ConfigureAwait(false);
			EmitSubmittedInput(threadId, turnId, starting, input, skills);
		});
	}

	private string RequestFor(long id, string threadId, string? turnId, CodexTurnInput input, IReadOnlyList<CodexSkill> skills) {
		if (input.Images.Count > 0 || skills.Count > 0) {
			string[] imagePaths = [.. input.Images.Select(image => image.Path)];
			return string.IsNullOrEmpty(turnId)
				? CodexAppServerProtocol.TurnStartWithInputs(id, threadId, input.Text, imagePaths, skills, _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), EffectiveModel(), EffectiveReasoningEffort(), EffectiveServiceTier())
				: CodexAppServerProtocol.TurnSteerWithInputs(id, threadId, turnId, input.Text, imagePaths, skills);
		}

		return string.IsNullOrEmpty(turnId)
			? CodexAppServerProtocol.TurnStart(id, threadId, input.Text, _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), EffectiveModel(), EffectiveReasoningEffort(), EffectiveServiceTier())
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
}
