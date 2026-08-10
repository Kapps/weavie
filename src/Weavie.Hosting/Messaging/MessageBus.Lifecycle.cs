namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
	internal void Disconnect(WebPeer peer) {
		foreach (var request in _requests) {
			if (request.Key.Peer == peer) {
				_ = request.Value.Cancellation.CancelAsync();
			}
		}

		SettleOutbound(peer, "The bound view disconnected before the request completed.", notifyPeer: false);
		if (_peers.TryRemove(peer, out var owner)) {
			RaisePeerDisconnected(owner);
		}
	}

	private void RaisePeerDisconnected(MessagePeer owner) {
		var handlers = PeerDisconnected;
		if (handlers is null) {
			return;
		}

		foreach (Action<MessagePeer> handler in handlers.GetInvocationList()) {
			_ = Task.Run(() => {
				try {
					handler(owner);
				} catch (Exception ex) {
					LogDiagnostic($"[bridge] peer-disconnect handler failed: {ex}");
				}
			});
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
								disconnected.Name).ToTransportMessage());
					} catch (Exception) {
						// The detached request is already settled regardless of transport state.
					}
				}

				disconnected.Completion.TrySetException(new InvalidOperationException(message));
			}
		}
	}

	internal Task QuiesceAsync() {
		lock (_lifecycle) {
			Volatile.Write(ref _accepting, 0);
			CancelDispatches();
			return PendingDispatchesLocked();
		}
	}

	private Task PendingDispatchesLocked() {
		var current = _afterResponseContext.Value;
		return Task.WhenAll(_dispatches
			.Where(dispatch => !ReferenceEquals(dispatch, current))
			.Select(dispatch => dispatch.Completion.Task));
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
