using Weavie.Core.Diagnostics;

namespace Weavie.Hosting.Messaging;

/// <summary>
/// One feature's endpoint on an owning host or session bus. It contains no transport, peer, request, or session
/// identity, so publishers and handlers cannot accidentally address another owner.
/// </summary>
public sealed class MessageFeatureChannel : IMessageFeatureTarget {
	private readonly MessageBus _bus;
	private readonly string _feature;

	internal MessageFeatureChannel(MessageBus bus, string feature) {
		ArgumentNullException.ThrowIfNull(bus);
		ArgumentException.ThrowIfNullOrEmpty(feature);
		_bus = bus;
		_feature = feature;
	}

	/// <summary>Registers a serialized request handler and returns its lifetime.</summary>
	public IDisposable Handle<TRequest, TResponse>(
		string name,
		Func<TRequest, CancellationToken, Task<TResponse>> handler) =>
		_bus.Handle(_feature, name, handler, SessionExecution.Serialized);

	internal IDisposable HandleAfterResponse<TRequest, TResponse>(
		string name,
		Func<TRequest, CancellationToken, Task<ResponseWithCompletion<TResponse>>> handler) =>
		_bus.HandleAfterResponse(_feature, name, handler, SessionExecution.Serialized);

	internal IDisposable HandleKeyedAfterResponse<TRequest, TResponse>(
		string name,
		Func<TRequest, string> lane,
		Func<TRequest, CancellationToken, Task<ResponseWithCompletion<TResponse>>> handler) =>
		_bus.HandleKeyedAfterResponse(_feature, name, lane, handler);

	internal IDisposable HandleAfterEvent<TEvent>(
		string name,
		Func<TEvent, CancellationToken, Task<Func<CancellationToken, Task>>> handler) =>
		_bus.HandleAfterEvent(_feature, name, handler, SessionExecution.Serialized);

	internal IDisposable HandleOwned<TRequest, TResponse>(
		string name,
		Func<TRequest, MessagePeer, CancellationToken, Task<TResponse>> handler) =>
		_bus.HandleOwned(_feature, name, handler, SessionExecution.Serialized);

	/// <summary>Registers a serialized event handler and returns its lifetime.</summary>
	public IDisposable Handle<TEvent>(
		string name,
		Func<TEvent, CancellationToken, Task> handler) =>
		_bus.Handle(_feature, name, handler, SessionExecution.Serialized);

	internal IDisposable HandleOwned<TEvent>(
		string name,
		Func<TEvent, MessagePeer, CancellationToken, Task> handler) =>
		_bus.HandleOwned(_feature, name, handler, SessionExecution.Serialized);

	internal IDisposable HandleOwned<TEvent>(
		string name,
		Func<MessagePeer, bool> admit,
		Func<TEvent, MessagePeer, CancellationToken, Task> handler) =>
		_bus.HandleOwnedWhen(_feature, name, admit, handler, SessionExecution.Serialized);

	internal MessageTargetFeature Target(MessagePeer peer) {
		ArgumentNullException.ThrowIfNull(peer);
		return peer.Target.Feature(_feature);
	}

	/// <summary>Registers a request handler that may run concurrently with other work in this feature.</summary>
	public IDisposable HandleConcurrent<TRequest, TResponse>(
		string name,
		Func<TRequest, CancellationToken, Task<TResponse>> handler) =>
		_bus.Handle(_feature, name, handler, SessionExecution.Concurrent);

	internal IDisposable HandleKeyed<TRequest, TResponse>(
		string name,
		Func<TRequest, string> lane,
		Func<TRequest, CancellationToken, Task<TResponse>> handler) =>
		_bus.HandleKeyed(_feature, name, lane, handler);

	/// <summary>Registers an event handler that may run concurrently with other work in this feature.</summary>
	public IDisposable HandleConcurrent<TEvent>(
		string name,
		Func<TEvent, CancellationToken, Task> handler) =>
		_bus.Handle(_feature, name, handler, SessionExecution.Concurrent);

	/// <summary>Publishes an event to every page attached to this feature's owner.</summary>
	public void Publish<T>(string name, T payload) => _bus.Publish(_feature, name, payload);

	/// <summary>Publishes an already-serialized JSON payload to every page attached to this feature's owner.</summary>
	public void PublishJson(string name, string payloadJson) => _bus.PublishJson(_feature, name, payloadJson);
}

internal sealed record ResponseWithCompletion<T>(T Payload, Func<CancellationToken, Task> AfterResponse);

internal interface IMessageFeatureTarget {
	void Publish<T>(string name, T payload);

	void PublishJson(string name, string payloadJson);
}

internal sealed class MessagePeer {
	private readonly WebPeer _peer;

	public MessagePeer(MessageBus bus, WebPeer peer) {
		_peer = peer;
		Target = new MessageTarget(bus, peer);
	}

	public MessageTarget Target { get; }

	internal bool Is(WebPeer peer) => _peer == peer;
}

internal sealed class MessageTarget {
	private readonly MessageBus _bus;
	private readonly WebPeer? _peer;

	public MessageTarget(MessageBus bus, WebPeer? peer) {
		_bus = bus;
		_peer = peer;
	}

	public MessageTargetFeature Feature(string name) {
		ArgumentException.ThrowIfNullOrEmpty(name);
		return new MessageTargetFeature(_bus, _peer, name);
	}
}

internal sealed class MessageTargetFeature : IMessageFeatureTarget {
	private readonly MessageBus _bus;
	private readonly WebPeer? _peer;
	private readonly string _feature;

	public MessageTargetFeature(MessageBus bus, WebPeer? peer, string feature) {
		_bus = bus;
		_peer = peer;
		_feature = feature;
	}

	public void Publish<T>(string name, T payload) {
		if (_peer is { } peer) {
			_bus.PublishTo(peer, _feature, name, payload);
		} else {
			_bus.Publish(_feature, name, payload);
		}
	}

	public void PublishJson(string name, string payloadJson) {
		if (_peer is { } peer) {
			_bus.PublishJsonTo(peer, _feature, name, payloadJson);
		} else {
			_bus.PublishJson(_feature, name, payloadJson);
		}
	}
}

internal sealed class SessionMessageBus : MessageBus {
	public SessionMessageBus(
		SessionAddress address,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> sendToPeer,
		Action<string> log)
		: this(
			address,
			broadcast,
			sendToPeer,
			new DiagnosticWorker(log)) {
	}

	private SessionMessageBus(
		SessionAddress address,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> sendToPeer,
		DiagnosticWorker diagnostics)
		: this(
			address,
			broadcast,
			sendToPeer,
			diagnostics,
			new MessageOperationRegistry(
				sendToPeer,
				diagnostics,
				MessageExecutionPolicy.Default,
				TimeProvider.System)) {
	}

	public SessionMessageBus(
		SessionAddress address,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> sendToPeer,
		DiagnosticWorker diagnostics,
		MessageOperationRegistry operations)
		: base(
			MessageScope.Session,
			address,
			broadcast,
			sendToPeer,
			diagnostics,
			ThreadPoolMessageHandlerExecutor.Instance,
			operations) {
	}

	public new SessionAddress Address => base.Address!;
}

internal sealed class HostMessageBus : MessageBus {
	public HostMessageBus(
		IUiDispatcher dispatcher,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> sendToPeer,
		DiagnosticWorker diagnostics,
		MessageOperationRegistry operations)
		: base(
			MessageScope.Host,
			null,
			broadcast,
			sendToPeer,
			diagnostics,
			new UiMessageHandlerExecutor(dispatcher),
			operations) {
	}
}
