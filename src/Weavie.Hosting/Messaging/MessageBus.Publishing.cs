using System.Text.Json;

namespace Weavie.Hosting.Messaging;

internal partial class MessageBus {
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

	internal void PublishJson(string feature, string name, string payloadJson) =>
		_broadcast(JsonEvent(feature, name, payloadJson));

	internal Task PublishJsonAsync(string feature, string name, string payloadJson, CancellationToken ct) =>
		PublishJsonAsync(null, feature, name, payloadJson, ct);

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

	internal void PublishJsonTo(WebPeer peer, string feature, string name, string payloadJson) =>
		_sendToPeer(peer, JsonEvent(feature, name, payloadJson));

	internal Task PublishJsonToAsync(WebPeer peer, string feature, string name, string payloadJson, CancellationToken ct) =>
		PublishJsonAsync(peer, feature, name, payloadJson, ct);

	private async Task PublishJsonAsync(WebPeer? peer, string feature, string name, string payloadJson, CancellationToken ct) {
		var message = JsonEvent(feature, name, payloadJson);
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _dispatchCancellation.Token);
		await (peer is { } target
			? _sendToPeerAsync(target, message, cancellation.Token)
			: _broadcastAsync(message, cancellation.Token)).ConfigureAwait(false);
	}

	private WebTransportMessage JsonEvent(string feature, string name, string payloadJson) {
		ArgumentException.ThrowIfNullOrEmpty(feature);
		ArgumentException.ThrowIfNullOrEmpty(name);
		ArgumentException.ThrowIfNullOrEmpty(payloadJson);
		ThrowIfClosed();
		using var document = JsonDocument.Parse(payloadJson);
		return MessageEnvelope.Event(Scope, Address, feature, name, document.RootElement.Clone()).ToTransportMessage();
	}
}
