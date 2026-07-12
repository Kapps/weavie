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
	}

	private async Task<JsonElement> ResumeThreadAsync(long requestId, string threadId) {
		try {
			return await _client.RequestAsync(
				requestId,
				CodexAppServerProtocol.ThreadResume(
					requestId, threadId, Model(), _context.Workspace, Sandbox(), ApprovalPolicy(), DeveloperInstructions()),
				CancellationToken.None).ConfigureAwait(false);
		} catch (InvalidOperationException ex) when (ex.Message.Contains("no rollout found", StringComparison.OrdinalIgnoreCase)) {
			_threads.Clear(_context.Workspace);
			Emit(new AgentPaneMessage {
				Type = "warning",
				ProviderId = "codex",
				Text = "Codex could not resume the saved empty thread, so Weavie started a new thread.",
				Status = "warning",
			});
			long startRequest = NextRequest();
			return await StartThreadAsync(startRequest).ConfigureAwait(false);
		}
	}

	private Task<JsonElement> StartThreadAsync(long requestId) =>
		_client.RequestAsync(
			requestId,
			CodexAppServerProtocol.ThreadStart(
				requestId, Model(), _context.Workspace, Sandbox(), ApprovalPolicy(), DeveloperInstructions()),
			CancellationToken.None);

	private void AdoptThread(string threadId) {
		lock (_gate) {
			_threadId = threadId;
		}

		FlushPendingInputs();
	}
}
