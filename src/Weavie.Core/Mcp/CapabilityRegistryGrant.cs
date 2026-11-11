using Weavie.Workflows;

namespace Weavie.Core.Mcp;

/// <summary>A conversation's revocable credential and stage-tool admission on the session's registry.</summary>
public sealed class CapabilityRegistryGrant : IAsyncDisposable {
	private readonly McpServer _server;
	private readonly Func<WorkflowStageCall?> _stage;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly Lock _gate = new();
	private readonly HashSet<TaskCompletionSource> _responses = [];
	private int _disposed;

	internal CapabilityRegistryGrant(McpServer server, AgentSessionCredential credential, Func<WorkflowStageCall?> stage, string? conversation) {
		_server = server;
		Credential = credential;
		_stage = stage;
		Conversation = conversation;
		Lifetime = _lifetime.Token;
	}

	/// <summary>The credential advertised only to the owning conversation.</summary>
	public AgentSessionCredential Credential { get; }

	/// <summary>The holder's name, or null when it is its session's main conversation.</summary>
	public string? Conversation { get; }
	internal CancellationToken Lifetime { get; }
	internal bool Live => Volatile.Read(ref _disposed) == 0;
	internal WorkflowStageCall? CaptureStage() => _stage();

	/// <summary>Outstanding completion replies; read after revocation to drain before closing the caller.</summary>
	public Task CompletionResponses {
		get { lock (_gate) return Task.WhenAll(_responses.Select(response => response.Task)); }
	}

	internal TaskCompletionSource BeginCompletionResponse() {
		lock (_gate) {
			if (!Live) throw new ToolUnavailableException("This conversation has ended.");
			var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			_responses.Add(response);
			return response;
		}
	}

	internal void EndCompletionResponse(TaskCompletionSource response) {
		lock (_gate) {
			response.TrySetResult();
			_responses.Remove(response);
		}
	}

	/// <summary>Refuses new calls and cancels work admitted through this credential.</summary>
	public async ValueTask DisposeAsync() {
		lock (_gate) {
			if (!Live) return;
			Volatile.Write(ref _disposed, 1);
		}
		_server.RemoveGrant(this);
		await _lifetime.CancelAsync().ConfigureAwait(false);
		_lifetime.Dispose();
	}
}
