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

	public void Broadcast(WebTransportMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		Send(new PendingSend(null, message));
	}

	public void Send(WebPeer peer, WebTransportMessage message) {
		ArgumentNullException.ThrowIfNull(message);
		Send(new PendingSend(peer, message));
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
			_transport.Send(peer, pending.Message);
		} else {
			_transport.Broadcast(pending.Message);
		}
	}

	private sealed record PendingSend(WebPeer? Peer, WebTransportMessage Message);
}
