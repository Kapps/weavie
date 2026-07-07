using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Handles user actions sent to a native Codex app-server session.</summary>
public sealed partial class CodexAppServerSession {
	/// <inheritdoc/>
	public void SubmitPrompt(string prompt) {
		ArgumentNullException.ThrowIfNull(prompt);
		var input = TakeTurnInput(prompt);
		if (input.Text.Length == 0 && input.Images.Count == 0) {
			return;
		}

		SubmitTurn(input);
	}

	/// <inheritdoc/>
	public void AttachImage(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			_pendingImages.Add(path);
		}

		Emit(new AgentPaneMessage {
			Type = "user-image",
			ProviderId = "codex",
			ThreadId = CurrentThreadId(),
			Text = path,
			Status = "attached",
		});
	}

	private CodexTurnInput TakeTurnInput(string prompt) {
		lock (_gate) {
			var input = new CodexTurnInput(prompt, [.. _pendingImages]);
			_pendingImages.Clear();
			return input;
		}
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

			long id = NextRequest();
			bool starting = string.IsNullOrEmpty(turnId);
			string request = RequestFor(id, threadId, turnId, input);
			await _client.RequestAsync(id, request, CancellationToken.None).ConfigureAwait(false);
			EmitSubmittedInput(threadId, turnId, starting, input);
		});
	}

	private string RequestFor(long id, string threadId, string? turnId, CodexTurnInput input) {
		if (input.Images.Count > 0) {
			return string.IsNullOrEmpty(turnId)
				? CodexAppServerProtocol.TurnStartWithImages(id, threadId, input.Text, input.Images, _context.Workspace, Sandbox(), ApprovalPolicy())
				: CodexAppServerProtocol.TurnSteerWithImages(id, threadId, turnId, input.Text, input.Images);
		}

		return string.IsNullOrEmpty(turnId)
			? CodexAppServerProtocol.TurnStart(id, threadId, input.Text, _context.Workspace, Sandbox(), ApprovalPolicy())
			: CodexAppServerProtocol.TurnSteer(id, threadId, turnId, input.Text);
	}

	private void EmitSubmittedInput(string threadId, string? turnId, bool starting, CodexTurnInput input) {
		if (input.Text.Length > 0) {
			Emit(new AgentPaneMessage {
				Type = starting ? "user-message" : "user-steer",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				Text = input.Text,
			});
		}

		foreach (string path in input.Images) {
			Emit(new AgentPaneMessage {
				Type = "user-image",
				ProviderId = "codex",
				ThreadId = threadId,
				TurnId = turnId,
				Text = path,
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
	public void ResolveApproval(string requestId, string decision) {
		ArgumentException.ThrowIfNullOrEmpty(requestId);
		ArgumentException.ThrowIfNullOrEmpty(decision);
		if (!_pendingRequests.TryGetValue(requestId, out var request)) {
			EmitError($"Codex approval request {requestId} is not pending.");
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
			EmitError($"Codex input request {requestId} is not pending.");
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
