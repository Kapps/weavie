using System.Text.Json;
using System.Threading.Channels;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class MessagePublicationTests {
	[Fact]
	public async Task PublicationWaitsForActivationAndDeliveryAndTargetsTheRequestingPeer() {
		var transport = new ControlledTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		var address = new SessionAddress("session", "incarnation");
		await using var endpoint = router.OpenSession(address);
		var feature = endpoint.Bus.Feature("review");
		var publication = feature.PublishJsonAsync("diff", "{\"version\":1}", CancellationToken.None);
		Assert.False(publication.IsCompleted);
		Assert.False(transport.Deliveries.Reader.TryRead(out _));

		endpoint.Activate(() => { });
		var broadcast = await transport.Deliveries.Reader.ReadAsync();
		Assert.Null(broadcast.Peer);
		Assert.False(publication.IsCompleted);
		Assert.True(MessageEnvelope.TryParse(broadcast.Message.Json, out var envelope));
		Assert.Equal(address, envelope!.Session);
		broadcast.Delivered.SetResult();
		await publication;

		using var handler = feature.HandleOwned<JsonElement>("snapshot", async (_, peer, ct) =>
			await feature.Target(peer).PublishJsonAsync("diff", "{\"version\":2}", ct));
		var peer = new WebPeer("requesting-page");
		var dispatch = router.RouteAsync(peer, MessageEnvelope.Event(
			MessageScope.Session, address, "review", "snapshot", JsonSerializer.SerializeToElement(new { })).ToJson());
		var targeted = await transport.Deliveries.Reader.ReadAsync();
		Assert.Equal(peer, targeted.Peer);
		Assert.False(dispatch.IsCompleted);
		targeted.Delivered.SetResult();
		await dispatch;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SessionQuiescenceCancelsPublicationBeforeActivationOrWhileBackpressured(bool activated) {
		var transport = new ControlledTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("session", "incarnation"));
		if (activated) endpoint.Activate(() => { });
		var publication = endpoint.Bus.Feature("review").PublishJsonAsync("diff", "{}", CancellationToken.None);
		if (activated) await transport.Deliveries.Reader.ReadAsync();
		Assert.False(publication.IsCompleted);
		await endpoint.QuiesceAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
	}

	private sealed record Delivery(WebPeer? Peer, WebTransportMessage Message, TaskCompletionSource Delivered);

	private sealed class ControlledTransport : IWebTransportHub {
		public Channel<Delivery> Deliveries { get; } = Channel.CreateUnbounded<Delivery>();
		public event Action<WebPeer, string>? MessageReceived { add { } remove { } }
		public event Action<WebPeer>? PeerDisconnected { add { } remove { } }

		public void Broadcast(WebTransportMessage message) => throw new InvalidOperationException("Publication must await delivery.");
		public void Send(WebPeer peer, WebTransportMessage message) => throw new InvalidOperationException("Publication must await delivery.");
		public Task BroadcastAsync(WebTransportMessage message, CancellationToken cancellationToken) =>
			DeliverAsync(null, message, cancellationToken);
		public Task SendAsync(WebPeer peer, WebTransportMessage message, CancellationToken cancellationToken) =>
			DeliverAsync(peer, message, cancellationToken);

		private Task DeliverAsync(WebPeer? peer, WebTransportMessage message, CancellationToken ct) {
			var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			Assert.True(Deliveries.Writer.TryWrite(new Delivery(peer, message, delivered)));
			return delivered.Task.WaitAsync(ct);
		}
	}
}
