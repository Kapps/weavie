namespace Weavie.Hosting.Messaging;

internal sealed class ViewBindings {
	private readonly object _gate = new();
	private readonly Dictionary<WebPeer, ViewBinding> _byPeer = [];
	private readonly Dictionary<SessionAddress, ViewBinding> _bySession = [];

	public ViewBinding[] Attach(WebPeer peer, SessionAddress session, string pageEpoch) {
		lock (_gate) {
			if (_byPeer.TryGetValue(peer, out var current)
				&& current.Session == session
				&& current.PageEpoch == pageEpoch
				&& _bySession.TryGetValue(session, out var currentPeer)
				&& currentPeer == current) {
				return [];
			}

			var detached = new List<ViewBinding>(2);
			if (_byPeer.Remove(peer, out var previous)
				&& _bySession.TryGetValue(previous.Session, out var previousPeer)
				&& previousPeer == previous) {
				_bySession.Remove(previous.Session);
				detached.Add(previous);
			}

			if (_bySession.Remove(session, out var replaced)) {
				_byPeer.Remove(replaced.Peer);
				detached.Add(replaced);
			}

			var binding = new ViewBinding(peer, session, pageEpoch);
			_byPeer[peer] = binding;
			_bySession[session] = binding;
			return [.. detached];
		}
	}

	public bool Detach(WebPeer peer, SessionAddress session, string pageEpoch) {
		lock (_gate) {
			if (_byPeer.TryGetValue(peer, out var attached)
				&& attached.Session == session
				&& attached.PageEpoch == pageEpoch) {
				_byPeer.Remove(peer);
				_bySession.Remove(session);
				return true;
			}

			return false;
		}
	}

	public bool TryGetPeer(SessionAddress session, out WebPeer peer) {
		lock (_gate) {
			if (_bySession.TryGetValue(session, out var binding)) {
				peer = binding.Peer;
				return true;
			}

			peer = default;
			return false;
		}
	}

	public bool IsBound(WebPeer peer, SessionAddress session) {
		lock (_gate) {
			return _byPeer.TryGetValue(peer, out var attached)
				&& attached.Session == session
				&& _bySession.TryGetValue(session, out var attachedPeer)
				&& attachedPeer == attached;
		}
	}

	public bool IsBound(MessagePeer peer, SessionAddress session) {
		lock (_gate) {
			return _bySession.TryGetValue(session, out var attached)
				&& peer.Is(attached.Peer)
				&& _byPeer.TryGetValue(attached.Peer, out var attachedSession)
				&& attachedSession == attached;
		}
	}

	public void Disconnect(WebPeer peer) {
		lock (_gate) {
			if (_byPeer.Remove(peer, out var session)
				&& _bySession.TryGetValue(session.Session, out var attached)
				&& attached == session) {
				_bySession.Remove(session.Session);
			}
		}
	}

	public ViewBinding? Remove(SessionAddress session) {
		lock (_gate) {
			if (_bySession.Remove(session, out var peer)
				&& _byPeer.TryGetValue(peer.Peer, out var attached)
				&& attached == peer) {
				_byPeer.Remove(peer.Peer);
				return peer;
			}

			return null;
		}
	}

	public void Clear() {
		lock (_gate) {
			_byPeer.Clear();
			_bySession.Clear();
		}
	}
}

internal sealed record ViewBinding(WebPeer Peer, SessionAddress Session, string PageEpoch);
