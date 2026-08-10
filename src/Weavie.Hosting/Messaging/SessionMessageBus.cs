using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Diagnostics;

namespace Weavie.Hosting.Messaging;

internal partial class MessageBus : IAsyncDisposable {
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private static readonly Func<MessagePeer, bool> AdmitEveryPeer = static _ => true;
	private readonly Action<WebTransportMessage> _broadcast;
	private readonly Action<WebPeer, WebTransportMessage> _sendToPeer;
	private readonly DiagnosticWorker _diagnostics;
	private readonly IMessageHandlerExecutor _handlerExecutor;
	private readonly MessageOperationRegistry _operations;
	private readonly object _lifecycle = new();
	private readonly Dictionary<(string Feature, string Name), HandlerRegistration> _handlers = [];
	private readonly Dictionary<string, FeatureLane> _featureLanes = [];
	private readonly ConcurrentDictionary<(WebPeer Peer, string Request), InboundRequest> _requests = new();
	private readonly ConcurrentDictionary<(WebPeer Peer, string Request), OutboundRequest> _outbound = new();
	private readonly ConcurrentDictionary<WebPeer, MessagePeer> _peers = new();
	private readonly HashSet<DispatchLifetime> _dispatches = [];
	private readonly AsyncLocal<DispatchLifetime?> _afterResponseContext = new();
	private readonly CancellationTokenSource _dispatchCancellation = new();
	private int _accepting = 1;
	private int _dispatchCancellationRequested;
	private long _requestSequence;
	private int _isClosed;
	private int _isFaulted;
	private string? _faultReason;

	public MessageBus(
		MessageScope scope,
		SessionAddress? address,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> sendToPeer,
		DiagnosticWorker diagnostics,
		IMessageHandlerExecutor handlerExecutor,
		MessageOperationRegistry operations) {
		if (scope == MessageScope.Session) {
			ArgumentNullException.ThrowIfNull(address);
		} else if (address is not null) {
			throw new ArgumentException("A host bus cannot have a session address.", nameof(address));
		}

		ArgumentNullException.ThrowIfNull(broadcast);
		ArgumentNullException.ThrowIfNull(sendToPeer);
		ArgumentNullException.ThrowIfNull(diagnostics);
		ArgumentNullException.ThrowIfNull(handlerExecutor);
		ArgumentNullException.ThrowIfNull(operations);
		Scope = scope;
		Address = address;
		_broadcast = broadcast;
		_sendToPeer = sendToPeer;
		_diagnostics = diagnostics;
		_handlerExecutor = handlerExecutor;
		_operations = operations;
		BroadcastTarget = new MessageTarget(this, null);
	}

	public MessageScope Scope { get; }

	public SessionAddress? Address { get; }

	internal MessageTarget BroadcastTarget { get; }

	internal event Action<MessagePeer>? PeerDisconnected;

	public bool Closed => Volatile.Read(ref _isClosed) != 0 || Volatile.Read(ref _isFaulted) != 0;

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
		Func<TEvent, CancellationToken, Task<Func<CancellationToken, Task>>> handler,
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
		_broadcast(envelope.ToTransportMessage());
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
		_broadcast(envelope.ToTransportMessage());
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
				JsonSerializer.SerializeToElement(payload, JsonOptions)).ToTransportMessage());
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
				document.RootElement.Clone()).ToTransportMessage());
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
				_ = request.Cancellation.CancelAsync();
			}

			return Task.CompletedTask;
		}

		if (envelope.Kind is not (MessageKind.Request or MessageKind.Event)) {
			return Task.CompletedTask;
		}

		Task<DispatchCompletion>? dispatch = null;
		DispatchLifetime? lifetime = null;
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
							admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
							var operation = _operations.Start(peer, envelope, OnOperationTimedOut);
							lifetime = new DispatchLifetime(operation);
							dispatch = RunHandlerAsync(
								owner,
								peer,
								envelope,
								registration,
								admitted.Task,
								operation);
							_dispatches.Add(lifetime);
							_ = dispatch.ContinueWith(
								(_, state) => ((MessageBus)state!).DispatchFinished(dispatch, lifetime),
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
			string closingError = Volatile.Read(ref _faultReason) ?? "The target endpoint is closing.";
			SendFailure(
				peer,
				envelope,
				closing
					? closingError
					: $"No handler is registered for {envelope.Feature}.{envelope.Name}.");
		} else if (!closing) {
			LogDiagnostic($"[bridge] no handler for endpoint event {envelope.Feature}.{envelope.Name}");
		}

		return Task.CompletedTask;
	}

	internal MessagePeer Peer(WebPeer peer) =>
		_peers.GetOrAdd(peer, candidate => new MessagePeer(this, candidate));

	internal Task DrainAsync() {
		lock (_lifecycle) {
			return PendingDispatchesLocked();
		}
	}

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
					JsonSerializer.SerializeToElement(payload, JsonOptions)).ToTransportMessage());
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
