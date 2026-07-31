namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
	internal void Disconnect(WebPeer peer) {
		foreach (var request in _requests) {
			if (request.Key.Peer == peer) {
				request.Value.Cancellation.Cancel();
			}
		}

		SettleOutbound(peer, "The bound view disconnected before the request completed.", notifyPeer: false);
		if (_peers.TryRemove(peer, out var owner)) {
			PeerDisconnected?.Invoke(owner);
		}
	}

	internal void ViewDetached(WebPeer peer) =>
		SettleOutbound(peer, "The request's view is no longer attached to this session.", notifyPeer: true);

	private void SettleOutbound(WebPeer peer, string message, bool notifyPeer) {
		foreach (var request in _outbound) {
			if (request.Key.Peer == peer
				&& _outbound.TryRemove(request.Key, out var disconnected)) {
				if (notifyPeer) {
					try {
						_sendToPeer(
							peer,
							MessageEnvelope.Cancel(
								Scope,
								Address,
								request.Key.Request,
								disconnected.Feature,
								disconnected.Name).ToJson());
					} catch (Exception) {
						// The detached request is already settled regardless of transport state.
					}
				}

				disconnected.Completion.TrySetException(new InvalidOperationException(message));
			}
		}
	}

	internal Task QuiesceAsync() {
		Task quiesce;
		lock (_lifecycle) {
			if (_quiesceTask is not null) {
				return _quiesceTask;
			}

			Volatile.Write(ref _accepting, 0);
			_quiesceTask = Task.WhenAll([.. _dispatches]);
			quiesce = _quiesceTask;
		}

		_dispatchCancellation.Cancel();
		return quiesce;
	}

	public async ValueTask DisposeAsync() {
		await QuiesceAsync().ConfigureAwait(false);
		if (Interlocked.Exchange(ref _isClosed, 1) != 0) {
			return;
		}

		foreach (var request in _outbound.Values) {
			request.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
		}

		_requests.Clear();
		_outbound.Clear();
		_peers.Clear();
		lock (_handlers) {
			_handlers.Clear();
		}

		foreach (var lane in _featureLanes.Values) {
			lane.Dispose();
		}

		_featureLanes.Clear();
		_dispatchCancellation.Dispose();
	}
}
