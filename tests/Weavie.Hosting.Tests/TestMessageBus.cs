using Weavie.Hosting.Messaging;

namespace Weavie.Hosting.Tests;

internal static class TestMessageBus {
	public static SessionMessageBus Create(
		SessionAddress address,
		Action<WebTransportMessage> broadcast,
		Action<WebPeer, WebTransportMessage> send,
		Action<string> log) => new(
			address, broadcast, send,
			(message, ct) => {
				ct.ThrowIfCancellationRequested();
				broadcast(message);
				return Task.CompletedTask;
			},
			(peer, message, ct) => {
				ct.ThrowIfCancellationRequested();
				send(peer, message);
				return Task.CompletedTask;
			}, log);
}
