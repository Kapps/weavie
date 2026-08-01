using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal sealed class HostMessageRouter : IAsyncDisposable {
	private readonly IWebTransportHub _transport;
	private readonly Action<string> _log;
	private readonly SessionMessageRouter _sessions;
	private readonly ViewBindings _views = new();
	private readonly object _viewLifecycle = new();

	public HostMessageRouter(IWebTransportHub transport, IUiDispatcher dispatcher, Action<string> log) {
		ArgumentNullException.ThrowIfNull(transport);
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(log);
		_transport = transport;
		_log = log;
		Host = new HostMessageBus(dispatcher, transport.Broadcast, transport.Send, log);
		_sessions = new SessionMessageRouter(transport.Send, log);
	}

	public HostMessageBus Host { get; }

	public SessionEndpoint OpenSession(SessionAddress address) {
		ArgumentNullException.ThrowIfNull(address);
		var transport = new SessionTransportGate(_transport);
		var bus = new SessionMessageBus(address, transport.Broadcast, transport.Send, _log);
		return new SessionEndpoint(this, bus, transport);
	}

	internal void AttachSession(SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(bus);
		lock (_viewLifecycle) {
			_sessions.Add(bus);
		}
	}

	internal void DetachSession(SessionMessageBus bus) {
		ArgumentNullException.ThrowIfNull(bus);
		lock (_viewLifecycle) {
			_sessions.Remove(bus);
			if (_views.Remove(bus.Address) is { } binding) {
				bus.ViewDetached(binding.Peer);
			}
		}
	}

	public Task RouteAsync(WebPeer peer, string json) {
		if (!MessageEnvelope.TryParse(json, out var envelope) || envelope is null) {
			_log("[bridge] rejected a malformed message envelope");
			return Task.CompletedTask;
		}

		if (envelope.Scope == MessageScope.Session
			&& envelope.Session is { } address
			&& envelope.Kind == MessageKind.Event
			&& envelope.Feature == "view"
			&& envelope.Name is "attach" or "detach") {
			if (!TryGetPageEpoch(envelope.Payload, out string pageEpoch)) {
				_log("[bridge] rejected a view binding without a page epoch");
				return Task.CompletedTask;
			}

			lock (_viewLifecycle) {
				if (!_sessions.TryGet(address, out _)) {
					return Task.CompletedTask;
				}

				if (envelope.Name == "attach") {
					foreach (var detached in _views.Attach(peer, address, pageEpoch)) {
						if (_sessions.TryGet(detached.Session, out var detachedBus)) {
							detachedBus.ViewDetached(detached.Peer);
						}
					}
				} else if (_views.Detach(peer, address, pageEpoch)
					&& _sessions.TryGet(address, out var detachedBus)) {
					detachedBus.ViewDetached(peer);
				}
			}

			return Task.CompletedTask;
		}

		return envelope.Scope switch {
			MessageScope.Host => Host.DispatchAsync(peer, envelope),
			MessageScope.Session => _sessions.RouteAsync(peer, envelope),
			_ => throw new ArgumentOutOfRangeException(nameof(envelope), envelope.Scope, "Unknown message scope."),
		};
	}

	private static bool TryGetPageEpoch(JsonElement payload, out string pageEpoch) {
		pageEpoch = string.Empty;
		if (payload.ValueKind != JsonValueKind.Object
			|| !payload.TryGetProperty("pageEpoch", out var element)
			|| element.ValueKind != JsonValueKind.String) {
			return false;
		}

		pageEpoch = element.GetString() ?? string.Empty;
		return pageEpoch.Length is > 0 and <= 128;
	}

	public void Disconnect(WebPeer peer) {
		lock (_viewLifecycle) {
			_views.Disconnect(peer);
			Host.Disconnect(peer);
			_sessions.Disconnect(peer);
		}
	}

	public Task<TResponse> RequestViewAsync<TRequest, TResponse>(
		SessionAddress address,
		string feature,
		string name,
		TRequest payload,
		CancellationToken ct) {
		lock (_viewLifecycle) {
			if (!_sessions.TryGet(address, out var bus)) {
				throw new InvalidOperationException("The target session is not live.");
			}

			if (!_views.TryGetPeer(address, out var peer)) {
				throw new InvalidOperationException("The session has no attached view.");
			}

			var request = bus.RequestAsync<TRequest, TResponse>(peer, feature, name, payload, ct);
			if (!_views.IsBound(peer, address)) {
				bus.ViewDetached(peer);
			}

			return request;
		}
	}

	public async Task<TResponse?> TryRequestViewAsync<TRequest, TResponse>(
		SessionAddress address,
		string feature,
		string name,
		TRequest payload,
		CancellationToken ct)
		where TResponse : class {
		Task<TResponse> request;
		lock (_viewLifecycle) {
			if (!_sessions.TryGet(address, out var bus)) {
				throw new InvalidOperationException("The target session is not live.");
			}

			if (!_views.TryGetPeer(address, out var peer)) {
				return null;
			}

			request = bus.RequestAsync<TRequest, TResponse>(peer, feature, name, payload, ct);
			if (!_views.IsBound(peer, address)) {
				bus.ViewDetached(peer);
			}
		}

		return await request.ConfigureAwait(false);
	}

	public bool PublishView<T>(
		SessionAddress address,
		string feature,
		string name,
		T payload) {
		lock (_viewLifecycle) {
			if (!_sessions.TryGet(address, out var bus)
				|| !_views.TryGetPeer(address, out var peer)) {
				return false;
			}

			bus.PublishTo(peer, feature, name, payload);
			return _views.IsBound(peer, address);
		}
	}

	internal bool IsViewBound(SessionAddress address, MessagePeer peer) =>
		_views.IsBound(peer, address);

	public async ValueTask DisposeAsync() {
		lock (_viewLifecycle) {
			_views.Clear();
		}
		await _sessions.DisposeAsync().ConfigureAwait(false);
		await Host.DisposeAsync().ConfigureAwait(false);
	}
}

internal sealed class SessionEndpoint : IAsyncDisposable {
	private readonly HostMessageRouter _router;
	private readonly SessionTransportGate _transport;
	private readonly object _lifecycle = new();
	private bool _active;
	private bool _detached;
	private int _disposed;

	public SessionEndpoint(
		HostMessageRouter router,
		SessionMessageBus bus,
		SessionTransportGate transport) {
		ArgumentNullException.ThrowIfNull(router);
		ArgumentNullException.ThrowIfNull(bus);
		ArgumentNullException.ThrowIfNull(transport);
		_router = router;
		_transport = transport;
		Bus = bus;
		View = new SessionView(router, bus.Address);
	}

	public SessionAddress Address => Bus.Address;

	public SessionMessageBus Bus { get; }

	public SessionView View { get; }

	public void Activate() {
		lock (_lifecycle) {
			ObjectDisposedException.ThrowIf(_detached, this);
			if (_active) {
				return;
			}

			_router.AttachSession(Bus);
			try {
				_transport.Activate();
				_active = true;
			} catch {
				_router.DetachSession(Bus);
				throw;
			}
		}
	}

	public Task QuiesceAsync() {
		lock (_lifecycle) {
			if (!_detached) {
				_detached = true;
				if (_active) {
					_router.DetachSession(Bus);
				}
			}
		}

		return Bus.QuiesceAsync();
	}

	public async ValueTask DisposeAsync() {
		await QuiesceAsync().ConfigureAwait(false);
		if (Interlocked.Exchange(ref _disposed, 1) == 0) {
			try {
				await Bus.DisposeAsync().ConfigureAwait(false);
			} finally {
				_transport.Close();
			}
		}
	}
}

/// <summary>
/// The transient page presentation bound to one exact session. Durable work belongs on the session's message
/// bus; this endpoint is only for actions that require the page currently displaying that session.
/// </summary>
public sealed class SessionView {
	private readonly HostMessageRouter _router;
	private readonly SessionAddress _address;

	internal SessionView(HostMessageRouter router, SessionAddress address) {
		_router = router;
		_address = address;
	}

	/// <summary>Returns one presentation-only feature channel owned by this session.</summary>
	public ViewFeatureChannel Feature(string name) {
		ArgumentException.ThrowIfNullOrEmpty(name);
		return new ViewFeatureChannel(this, name);
	}

	internal bool IsBound(MessagePeer peer) => _router.IsViewBound(_address, peer);

	internal bool Publish<T>(string feature, string name, T payload) =>
		_router.PublishView(_address, feature, name, payload);

	internal Task<TResponse> RequestAsync<TRequest, TResponse>(
		string feature,
		string name,
		TRequest payload,
		CancellationToken ct) =>
		_router.RequestViewAsync<TRequest, TResponse>(_address, feature, name, payload, ct);

	internal Task<TResponse?> TryRequestAsync<TRequest, TResponse>(
		string feature,
		string name,
		TRequest payload,
		CancellationToken ct)
		where TResponse : class =>
		_router.TryRequestViewAsync<TRequest, TResponse>(_address, feature, name, payload, ct);
}

/// <summary>A feature on one session's currently attached page presentation.</summary>
public sealed class ViewFeatureChannel {
	private readonly SessionView _view;
	private readonly string _feature;

	internal ViewFeatureChannel(SessionView view, string feature) {
		_view = view;
		_feature = feature;
	}

	/// <summary>Attempts to send a transient event to the page displaying this session.</summary>
	public bool TryPublish<T>(string name, T payload) =>
		_view.Publish(_feature, name, payload);

	/// <summary>Runs a transient request on the page displaying this session.</summary>
	public Task<TResponse> RequestAsync<TRequest, TResponse>(
		string name,
		TRequest payload,
		CancellationToken ct) =>
		_view.RequestAsync<TRequest, TResponse>(_feature, name, payload, ct);

	/// <summary>Runs a transient request when this session has an attached page, or returns null when it does not.</summary>
	public Task<TResponse?> TryRequestAsync<TRequest, TResponse>(
		string name,
		TRequest payload,
		CancellationToken ct)
		where TResponse : class =>
		_view.TryRequestAsync<TRequest, TResponse>(_feature, name, payload, ct);
}
