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
		await _client.DisposeAsync().ConfigureAwait(false);
		await _context.Registry.DisposeAsync().ConfigureAwait(false);
	}

	private void OnClientStarted(int attempt) {
		lock (_gate) {
			_threadId = null;
			_turnId = null;
			_threadPersisted = false;
		}
		_pendingRequests.Clear();

		Run(InitializeAsync);
	}

	private void OnProcessStateChanged(Weavie.Core.Processes.SupervisorStateChanged change) =>
		_context.Events.Observe(new AgentProcessChanged(change));

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
