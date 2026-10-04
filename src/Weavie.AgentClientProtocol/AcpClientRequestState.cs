namespace Weavie.AgentClientProtocol;

/// <summary>One agent-to-client request awaiting Weavie's single response.</summary>
internal sealed class AcpClientRequestState : IDisposable {
	private readonly CancellationTokenSource _cancellation;
	private readonly Lock _gate = new();
	private bool _completed;
	private bool _published;

	public AcpClientRequestState(AcpClientRequest request) {
		Request = request;
		_cancellation = new CancellationTokenSource();
		Token = _cancellation.Token;
	}

	public AcpClientRequest Request { get; }

	public CancellationToken Token { get; }

	public bool TryComplete() => TryComplete(out _);

	public bool TryComplete(out bool published) {
		lock (_gate) {
			published = false;
			if (_completed) return false;
			_completed = true;
			published = _published;
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

	public bool PublishDeferred(Action publish) {
		ArgumentNullException.ThrowIfNull(publish);
		lock (_gate) {
			if (_completed) return false;
			publish();
			_published = true;
			return true;
		}
	}

	public void Dispose() => _cancellation.Dispose();
}
