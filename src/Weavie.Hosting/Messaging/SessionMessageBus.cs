using System.Collections.Concurrent;
using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal partial class MessageBus : IAsyncDisposable {
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly Func<MessagePeer, bool> AdmitEveryPeer = static _ => true;
	private readonly Action<string> _broadcast;
	private readonly Action<WebPeer, string> _sendToPeer;
	private readonly Action<string> _log;
	private readonly IMessageHandlerExecutor _handlerExecutor;
	private readonly object _lifecycle = new();
	private readonly Dictionary<(string Feature, string Name), HandlerRegistration> _handlers = [];
	private readonly Dictionary<string, FeatureLane> _featureLanes = [];
	private readonly ConcurrentDictionary<(WebPeer Peer, string Request), InboundRequest> _requests = new();
	private readonly ConcurrentDictionary<(WebPeer Peer, string Request), OutboundRequest> _outbound = new();
	private readonly ConcurrentDictionary<WebPeer, MessagePeer> _peers = new();
	private readonly HashSet<Task> _dispatches = [];
	private readonly CancellationTokenSource _dispatchCancellation = new();
	private Task? _quiesceTask;
	private int _accepting = 1;
	private long _requestSequence;
	private int _isClosed;

	public MessageBus(
		MessageScope scope,
		SessionAddress? address,
		Action<string> broadcast,
		Action<WebPeer, string> sendToPeer,
		Action<string> log,
		IMessageHandlerExecutor handlerExecutor) {
		if (scope == MessageScope.Session) {
			ArgumentNullException.ThrowIfNull(address);
		} else if (address is not null) {
			throw new ArgumentException("A host bus cannot have a session address.", nameof(address));
		}

		ArgumentNullException.ThrowIfNull(broadcast);
		ArgumentNullException.ThrowIfNull(sendToPeer);
		ArgumentNullException.ThrowIfNull(log);
		ArgumentNullException.ThrowIfNull(handlerExecutor);
		Scope = scope;
		Address = address;
		_broadcast = broadcast;
		_sendToPeer = sendToPeer;
		_log = log;
		_handlerExecutor = handlerExecutor;
		BroadcastTarget = new MessageTarget(this, null);
	}

	public MessageScope Scope { get; }

	public SessionAddress? Address { get; }

	internal MessageTarget BroadcastTarget { get; }

	internal event Action<MessagePeer>? PeerDisconnected;

	public bool Closed => Volatile.Read(ref _isClosed) != 0;

	private bool Accepting => Volatile.Read(ref _accepting) != 0;

	public MessageFeatureChannel Feature(string name) {
		ArgumentException.ThrowIfNullOrEmpty(name);
		return new MessageFeatureChannel(this, name);
	}

	internal IDisposable Handle<TRequest, TResponse>(
		string feature,
		string name,
		Func<TRequest, CancellationToken, Task<TResponse>> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(handler);
		return RegisterHandler(
			feature,
			name,
			async (_, payload, ct) => {
				var request = payload.Deserialize<TRequest>(JsonOptions)
					?? throw new JsonException($"Session handler {feature}.{name} received a null payload.");
				var response = await handler(request, ct).ConfigureAwait(false);
				return new HandlerResponse(
					JsonSerializer.SerializeToElement(response, JsonOptions),
					null);
			},
			execution,
			AdmitEveryPeer);
	}

	internal IDisposable HandleAfterResponse<TRequest, TResponse>(
		string feature,
		string name,
		Func<TRequest, CancellationToken, Task<ResponseWithCompletion<TResponse>>> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(handler);
		return RegisterHandler(
			feature,
			name,
			async (_, payload, ct) => {
				var request = payload.Deserialize<TRequest>(JsonOptions)
					?? throw new JsonException($"Session handler {feature}.{name} received a null payload.");
				var response = await handler(request, ct).ConfigureAwait(false);
				return new HandlerResponse(
					JsonSerializer.SerializeToElement(response.Payload, JsonOptions),
					response.AfterResponse);
			},
			execution,
			AdmitEveryPeer);
	}

	internal IDisposable HandleAfterEvent<TEvent>(
		string feature,
		string name,
		Func<TEvent, CancellationToken, Task<Func<Task>>> handler,
		SessionExecution execution) =>
		HandleAfterResponse<TEvent, NoResponse>(
			feature,
			name,
			async (message, ct) => new ResponseWithCompletion<NoResponse>(
				NoResponse.Value,
				await handler(message, ct).ConfigureAwait(false)),
			execution);

	internal IDisposable HandleOwned<TRequest, TResponse>(
		string feature,
		string name,
		Func<TRequest, MessagePeer, CancellationToken, Task<TResponse>> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(handler);
		return HandleOwnedWhen(
			feature,
			name,
			AdmitEveryPeer,
			handler,
			execution);
	}

	internal IDisposable HandleOwnedWhen<TRequest, TResponse>(
		string feature,
		string name,
		Func<MessagePeer, bool> admit,
		Func<TRequest, MessagePeer, CancellationToken, Task<TResponse>> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(admit);
		ArgumentNullException.ThrowIfNull(handler);
		return RegisterHandler(
			feature,
			name,
			async (peer, payload, ct) => {
				var request = payload.Deserialize<TRequest>(JsonOptions)
					?? throw new JsonException($"Session handler {feature}.{name} received a null payload.");
				var response = await handler(request, peer, ct).ConfigureAwait(false);
				return new HandlerResponse(
					JsonSerializer.SerializeToElement(response, JsonOptions),
					null);
			},
			execution,
			admit);
	}

	private IDisposable RegisterHandler(
		string feature,
		string name,
		Func<MessagePeer, JsonElement, CancellationToken, Task<HandlerResponse>> handler,
		SessionExecution execution,
		Func<MessagePeer, bool> admit) {
		var key = (feature, name);
		lock (_lifecycle) {
			if (!Accepting || Closed) {
				throw new ObjectDisposedException(GetType().Name);
			}

			var registration = new HandlerRegistration(
				handler,
				execution,
				GetFeatureLane(feature),
				admit);
			lock (_handlers) {
				if (!_handlers.TryAdd(key, registration)) {
					throw new InvalidOperationException($"A handler for {feature}.{name} is already registered.");
				}
			}
		}

		return new Registration(() => {
			lock (_handlers) {
				_handlers.Remove(key);
			}

		});
	}

	internal IDisposable Handle<TEvent>(
		string feature,
		string name,
		Func<TEvent, CancellationToken, Task> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(handler);
		return Handle<TEvent, NoResponse>(
			feature,
			name,
			async (message, ct) => {
				await handler(message, ct).ConfigureAwait(false);
				return NoResponse.Value;
			},
			execution);
	}

	internal IDisposable HandleOwned<TEvent>(
		string feature,
		string name,
		Func<TEvent, MessagePeer, CancellationToken, Task> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(handler);
		return HandleOwned<TEvent, NoResponse>(
			feature,
			name,
			async (message, peer, ct) => {
				await handler(message, peer, ct).ConfigureAwait(false);
				return NoResponse.Value;
			},
			execution);
	}

	internal IDisposable HandleOwnedWhen<TEvent>(
		string feature,
		string name,
		Func<MessagePeer, bool> admit,
		Func<TEvent, MessagePeer, CancellationToken, Task> handler,
		SessionExecution execution) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentNullException.ThrowIfNull(admit);
		ArgumentNullException.ThrowIfNull(handler);
		return HandleOwnedWhen<TEvent, NoResponse>(
			feature,
			name,
			admit,
			async (message, peer, ct) => {
				await handler(message, peer, ct).ConfigureAwait(false);
				return NoResponse.Value;
			},
			execution);
	}

	internal void Publish<T>(string feature, string name, T payload) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ThrowIfClosed();
		var envelope = MessageEnvelope.Event(
			Scope,
			Address,
			feature,
			name,
			JsonSerializer.SerializeToElement(payload, JsonOptions));
		_broadcast(envelope.ToJson());
	}

	internal void PublishJson(string feature, string name, string payloadJson) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentException.ThrowIfNullOrEmpty(payloadJson);
		ThrowIfClosed();
		using var document = JsonDocument.Parse(payloadJson);
		var envelope = MessageEnvelope.Event(
			Scope,
			Address,
			feature,
			name,
			document.RootElement.Clone());
		_broadcast(envelope.ToJson());
	}

	internal void PublishTo<T>(WebPeer peer, string feature, string name, T payload) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ThrowIfClosed();
		_sendToPeer(
			peer,
			MessageEnvelope.Event(
				Scope,
				Address,
				feature,
				name,
				JsonSerializer.SerializeToElement(payload, JsonOptions)).ToJson());
	}

	internal void PublishJsonTo(WebPeer peer, string feature, string name, string payloadJson) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentException.ThrowIfNullOrEmpty(payloadJson);
		ThrowIfClosed();
		using var document = JsonDocument.Parse(payloadJson);
		_sendToPeer(
			peer,
			MessageEnvelope.Event(
				Scope,
				Address,
				feature,
				name,
				document.RootElement.Clone()).ToJson());
	}

	internal Task DispatchAsync(WebPeer peer, MessageEnvelope envelope) {
		ArgumentNullException.ThrowIfNull(envelope);
		if (envelope.Scope != Scope || envelope.Session != Address) {
			throw new InvalidOperationException("A message bus cannot dispatch an envelope for another endpoint.");
		}

		if (envelope.Kind == MessageKind.Response) {
			ReceiveResponse(peer, envelope);
			return Task.CompletedTask;
		}

		if (envelope.Kind == MessageKind.Cancel) {
			if (envelope.RequestId is { } cancelId
				&& _requests.TryGetValue((peer, cancelId), out var request)
				&& request.Feature == envelope.Feature
				&& request.Name == envelope.Name) {
				request.Cancellation.Cancel();
			}

			return Task.CompletedTask;
		}

		if (envelope.Kind is not (MessageKind.Request or MessageKind.Event)) {
			return Task.CompletedTask;
		}

		Task<Func<Task>?>? dispatch = null;
		TaskCompletionSource? admitted = null;
		bool closing;
		bool rejected = false;
		lock (_lifecycle) {
			closing = !Accepting;
			if (!closing) {
				lock (_handlers) {
					if (_handlers.TryGetValue((envelope.Feature, envelope.Name), out var registration)) {
						var owner = Peer(peer);
						rejected = !registration.Admits(owner);
						if (!rejected) {
							admitted = new TaskCompletionSource();
							dispatch = RunHandlerAsync(
								owner,
								peer,
								envelope,
								registration,
								admitted.Task);
							_dispatches.Add(dispatch);
							_ = dispatch.ContinueWith(
								(_, state) => ((MessageBus)state!).DispatchFinished(dispatch),
								this,
								CancellationToken.None,
								TaskContinuationOptions.ExecuteSynchronously,
								TaskScheduler.Default);
						}
					}
				}
			}
		}

		if (dispatch is not null) {
			admitted!.SetResult();
			return dispatch;
		}

		if (rejected) {
			if (envelope.Kind == MessageKind.Request) {
				SendFailure(peer, envelope, "The peer is not admitted to this handler.");
			}
			return Task.CompletedTask;
		}

		if (envelope.Kind == MessageKind.Request) {
			SendFailure(
				peer,
				envelope,
				closing
					? "The target endpoint is closing."
					: $"No handler is registered for {envelope.Feature}.{envelope.Name}.");
		} else if (!closing) {
			_log($"[bridge] no handler for endpoint event {envelope.Feature}.{envelope.Name}");
		}

		return Task.CompletedTask;
	}

	internal MessagePeer Peer(WebPeer peer) =>
		_peers.GetOrAdd(peer, candidate => new MessagePeer(this, candidate));

	internal async Task<TResponse> RequestAsync<TRequest, TResponse>(
		WebPeer peer,
		string feature,
		string name,
		TRequest payload,
		CancellationToken ct) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ThrowIfClosed();
		string requestId = $"server-{Interlocked.Increment(ref _requestSequence)}";
		var request = new OutboundRequest(feature, name);
		if (!_outbound.TryAdd((peer, requestId), request)) {
			throw new InvalidOperationException($"Request '{requestId}' is already running.");
		}

		try {
			ct.ThrowIfCancellationRequested();
			_sendToPeer(
				peer,
				MessageEnvelope.Request(
					Scope,
					Address,
					requestId,
					feature,
					name,
					JsonSerializer.SerializeToElement(payload, JsonOptions)).ToJson());
			using var cancellation = ct.Register(
				() => CancelOutbound(peer, requestId, feature, name, ct));
			var response = await request.Completion.Task.ConfigureAwait(false);
			return response.Deserialize<TResponse>(JsonOptions)!;
		} catch {
			_outbound.TryRemove((peer, requestId), out _);
			throw;
		} finally {
			_outbound.TryRemove((peer, requestId), out _);
		}
	}
}
