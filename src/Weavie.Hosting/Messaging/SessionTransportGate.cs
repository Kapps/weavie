namespace Weavie.Hosting.Messaging;

internal sealed class SessionTransportGate {
	private readonly object _gate = new();
	private readonly IWebTransportHub _transport;
	private readonly List<PendingSend> _pending = [];
	private bool _active;
	private bool _closed;

	public SessionTransportGate(IWebTransportHub transport) {
		ArgumentNullException.ThrowIfNull(transport);
		_transport = transport;
	}

	public void Broadcast(string json) {
		ArgumentNullException.ThrowIfNull(json);
		Send(new PendingSend(null, json));
	}

	public void Send(WebPeer peer, string json) {
		ArgumentNullException.ThrowIfNull(json);
		Send(new PendingSend(peer, json));
	}

	public void Activate() {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closed, this);
			if (_active) {
				return;
			}

			foreach (var pending in _pending) {
				Deliver(pending);
			}

			_pending.Clear();
			_active = true;
		}
	}

	public void Close() {
		lock (_gate) {
			_closed = true;
			_pending.Clear();
		}
	}

	private void Send(PendingSend pending) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closed, this);
			if (!_active) {
				_pending.Add(pending);
				return;
			}

			Deliver(pending);
		}
	}

	private void Deliver(PendingSend pending) {
		if (pending.Peer is { } peer) {
			_transport.Send(peer, pending.Json);
		} else {
			_transport.Broadcast(pending.Json);
		}
	}

	private sealed record PendingSend(WebPeer? Peer, string Json);
}
