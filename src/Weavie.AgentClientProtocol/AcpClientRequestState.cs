namespace Weavie.AgentClientProtocol;

/// <summary>One agent-to-client request awaiting Weavie's single response.</summary>
internal sealed class AcpClientRequestState : IDisposable {
	private readonly CancellationTokenSource _cancellation;
	private readonly Lock _gate = new();
	private bool _completed;

	public AcpClientRequestState(AcpClientRequest request, CancellationToken lifetime) {
		Request = request;
		_cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
		Token = _cancellation.Token;
	}

	public AcpClientRequest Request { get; }

	public CancellationToken Token { get; }

	public bool Completed {
		get { lock (_gate) return _completed; }
	}

	public bool TryComplete() {
		lock (_gate) {
			if (_completed) return false;
			_completed = true;
			return true;
		}
	}

	public bool TryCancel() {
		lock (_gate) {
			if (_completed) return false;
			_completed = true;
		}
		_cancellation.Cancel();
		return true;
	}

	public void Dispose() => _cancellation.Dispose();
}
