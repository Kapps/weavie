using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal sealed class SessionMessageRouter : IAsyncDisposable {
	private readonly Dictionary<SessionAddress, SessionMessageBus> _sessions = [];
	private readonly Action<WebPeer, string> _sendToPeer;
	private readonly Action<string> _log;

	public SessionMessageRouter(Action<WebPeer, string> sendToPeer, Action<string> log) {
		ArgumentNullException.ThrowIfNull(sendToPeer);
		ArgumentNullException.ThrowIfNull(log);
		_sendToPeer = sendToPeer;
		_log = log;
	}

	public void Add(SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(bus);
		lock (_sessions) {
			if (!_sessions.TryAdd(bus.Address, bus)) {
				throw new InvalidOperationException(
					$"Session {bus.Address.Slot}/{bus.Address.Incarnation} is already registered.");
			}
		}
	}

	public void Remove(SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(bus);
		lock (_sessions) {
			_sessions.Remove(bus.Address);
		}
	}

	public bool TryGet(SessionAddress address, out SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(address);
		lock (_sessions) {
			return _sessions.TryGetValue(address, out bus!) && !bus.Closed;
		}
	}

	public Task RouteAsync(WebPeer peer, MessageEnvelope envelope) {
		ArgumentNullException.ThrowIfNull(envelope);
		if (envelope.Scope != MessageScope.Session || envelope.Session is null) {
			throw new InvalidOperationException("The session router only accepts session envelopes.");
		}

		if (TryGet(envelope.Session, out var bus)) {
			return bus.DispatchAsync(peer, envelope);
		}

		if (envelope.Kind == MessageKind.Request) {
			_sendToPeer(
				peer,
				MessageEnvelope.SessionResponse(
					envelope.Session,
					envelope.RequestId!,
					envelope.Feature,
					envelope.Name,
					JsonSerializer.SerializeToElement<object?>(null),
					"The target session is not live.").ToJson());
		} else {
			_log(
				$"[bridge] rejected {envelope.Feature}.{envelope.Name} for stale session "
				+ $"{envelope.Session.Slot}/{envelope.Session.Incarnation}");
		}

		return Task.CompletedTask;
	}

	public void Disconnect(WebPeer peer) {
		SessionMessageBus[] sessions;
		lock (_sessions) {
			sessions = [.. _sessions.Values];
		}

		foreach (var session in sessions) {
			session.Disconnect(peer);
		}
	}

	public Task DrainAsync() {
		lock (_sessions) {
			return Task.WhenAll(_sessions.Values.Select(session => session.DrainAsync()));
		}
	}

	public async ValueTask DisposeAsync() {
		SessionMessageBus[] sessions;
		lock (_sessions) {
			sessions = [.. _sessions.Values];
			_sessions.Clear();
		}

		foreach (var session in sessions) {
			await session.DisposeAsync().ConfigureAwait(false);
		}
	}
}
