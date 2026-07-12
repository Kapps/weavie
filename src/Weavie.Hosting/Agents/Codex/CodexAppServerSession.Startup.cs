using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Agents.Codex;

namespace Weavie.Hosting.Agents.Codex;

public sealed partial class CodexAppServerSession {
	/// <inheritdoc/>
	public void Start() {
		lock (_gate) {
			if (_started) {
				return;
			}

			_started = true;
		}

		_client.Start();
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		try {
			await _client.DisposeAsync().ConfigureAwait(false);
		} finally {
			CompleteWorkspaceTurn();
			await _context.Registry.DisposeAsync().ConfigureAwait(false);
		}
	}

	private void OnClientStarted(int attempt) {
		CompleteWorkspaceTurn();
		lock (_gate) {
			_threadId = null;
			_turnId = null;
			_turnStarting = false;
			_threadPersisted = false;
			_fileChangeSummaries.Clear();
		}

		RetractPendingRequests();
		Run(InitializeAsync);
	}
	// A restarted app-server no longer knows the requests the dead process asked, so leaving their cards
	// pending would wedge the pane on an Accept that can never reach anything: resolve each card and say why.
	private void RetractPendingRequests() {
		foreach (string requestId in _pendingRequests.Keys) {
			if (!_pendingRequests.TryRemove(requestId, out var request)) {
				continue;
			}

			// Recorded as resolved so a decision click racing this retraction stays a silent no-op.
			_resolvedRequests[request.Id] = 0;
			EmitCancelledResolution(
				request.Id,
				CodexApprovalResponses.CanResolve(request.Method) ? "approval-resolved" : "input-resolved");
			Emit(new AgentPaneMessage {
				Type = "warning",
				ProviderId = "codex",
				ItemId = request.Id,
				ItemType = request.Method,
				Summary = "Codex restarted; this pending request was discarded",
				Status = "warning",
			});
		}
	}

	private void OnProcessStateChanged(Weavie.Core.Processes.SupervisorStateChanged change) {
		if (change.ExitCode is not null) {
			CompleteWorkspaceTurn();
		}
		_context.Events.Observe(new AgentProcessChanged(change));
	}

	private async Task InitializeAsync() {
		long initialize = NextRequest();
		await _client.RequestAsync(initialize, CodexAppServerProtocol.Initialize(initialize, "0.1.0"), CancellationToken.None)
			.ConfigureAwait(false);
		_client.Notify(CodexAppServerProtocol.Initialized());
		var launch = _threads.Resolve(_context.Workspace);
		long threadRequest = NextRequest();
		var result = launch.Resume && !string.IsNullOrEmpty(launch.ThreadId)
			? await ResumeThreadAsync(threadRequest, launch.ThreadId).ConfigureAwait(false)
			: await StartThreadAsync(threadRequest).ConfigureAwait(false);
		AdoptThread(CodexThreadResults.ReadThreadId(result));
		HydrateTranscript(result);
		FlushPendingInputs();
		await LoadControlsAsync().ConfigureAwait(false);
	}

	private async Task<JsonElement> ResumeThreadAsync(long requestId, string threadId) {
		try {
			return await _client.RequestAsync(
				requestId,
				CodexAppServerProtocol.ThreadResume(
					requestId, threadId, EffectiveModel(), _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), DeveloperInstructions()),
				CancellationToken.None).ConfigureAwait(false);
		} catch (CodexRequestException ex) {
			return await StartFreshAfterFailedResumeAsync(ex).ConfigureAwait(false);
		}
	}

	// Codex rejected the saved thread (rollout missing, corrupt, format drift): start fresh, and only then
	// drop the stale mapping and transcript — a start failure propagates with both intact.
	private async Task<JsonElement> StartFreshAfterFailedResumeAsync(CodexRequestException resumeFailure) {
		long startRequest = NextRequest();
		var result = await StartThreadAsync(startRequest).ConfigureAwait(false);
		_threads.Clear(_context.Workspace);
		Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
		Emit(new AgentPaneMessage {
			Type = "warning",
			ProviderId = "codex",
			Summary = "Codex could not resume the saved thread, so Weavie started a new one.",
			Text = resumeFailure.Detail,
			Status = "warning",
			PayloadJson = resumeFailure.Payload,
		});
		return result;
	}

	private Task<JsonElement> StartThreadAsync(long requestId) =>
		_client.RequestAsync(
			requestId,
			CodexAppServerProtocol.ThreadStart(
				requestId, EffectiveModel(), _context.Workspace, EffectiveSandbox(), EffectiveApprovalPolicy(), DeveloperInstructions()),
			CancellationToken.None);

	private void AdoptThread(string threadId) {
		lock (_gate) {
			_threadId = threadId;
		}
	}

	private void HydrateTranscript(JsonElement result) {
		var messages = CodexPaneMessages.FromThreadSnapshot(result);
		if (messages.Count == 0) {
			return;
		}

		Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "codex" });
		foreach (var message in messages) {
			Emit(message);
		}
	}
}
