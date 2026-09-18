using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Hosting;
using Xunit;

namespace Weavie.Headless.Tests;

// Reproduces the remote-only "stuck connecting" bug: a HEALTHY but network-slow page connection is dropped
// when a synchronous push burst exceeds the bridge's bounded outbox. On loopback the send loop drains in
// microseconds so this never fires; over a real WSS link (or here, a deliberately stalled send) a large
// synchronous push burst fills the 512-deep outbox faster than it drains, and the worker aborts the client.
public sealed class OutboxFloodTests {
	[Fact]
	public async Task A_burst_larger_than_the_outbox_drops_a_slow_but_alive_connection() {
		var bridge = new WebSocketHostBridge();
		var socket = new StalledSendSocket();
		var serve = bridge.ServeAsync(socket, CancellationToken.None);

		// Prime one frame and wait for the send loop to stall inside SendAsync (a slow network): the fair outbox
		// keeps that logical message in its single outstanding-count bound until the send completes.
		bridge.Broadcast(Message("agent", "{\"type\":\"agent-pane\",\"n\":-1}"));
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));

		// Push past the remaining capacity in a tight, await-free loop, proving the bridge's explicit memory bound
		// independently of transcript paging.
		for (int i = 0; i < 600; i++) {
			bridge.Broadcast(Message("agent", $"{{\"type\":\"agent-pane\",\"n\":{i}}}"));
		}

		// The bridge treated the backlog as a dead peer and aborted the socket — even though it is alive and
		// its send loop is still making progress. This abort is what surfaces client-side as the endless
		// reconnect loop.
		Assert.True(await socket.Aborted.WaitAsync(TimeSpan.FromSeconds(5)));

		socket.ReleaseSends();
		await serve.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task AwaitedLargeBurstDrainsWithoutDroppingThePeer() {
		var bridge = new WebSocketHostBridge();
		var socket = new StalledSendSocket();
		using var stopping = new CancellationTokenSource();
		var serve = bridge.ServeAsync(socket, stopping.Token);
		const int count = 30;
		string payload = new('a', 700_000);
		async Task PublishAsync() {
			for (int index = 0; index < count; index++) {
				await bridge.BroadcastAsync(Message("review", payload), CancellationToken.None);
			}
		}

		var published = PublishAsync();
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));
		Assert.False(published.IsCompleted);
		Assert.Equal(WebSocketState.Open, socket.State);
		socket.ReleaseSends();
		await published.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(count, socket.Messages.Select(json => {
			using var document = JsonDocument.Parse(json);
			return document.RootElement.GetProperty("$weavieChunk").GetProperty("id").GetString();
		}).Distinct().Count());
		Assert.Equal(WebSocketState.Open, socket.State);
		await stopping.CancelAsync();
		await serve;
	}

	[Fact]
	public async Task AwaitedPublicationWaitsForCapacityAndPreservesRouteOrder() {
		var bridge = new WebSocketHostBridge();
		var socket = new StalledSendSocket();
		using var stopping = new CancellationTokenSource();
		var serve = bridge.ServeAsync(socket, stopping.Token);
		for (int index = 0; index < 512; index++) bridge.Broadcast(Message("review", index.ToString()));
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));
		var published = bridge.BroadcastAsync(Message("review", "last"), CancellationToken.None);
		Assert.False(published.IsCompleted);
		Assert.Equal(WebSocketState.Open, socket.State);
		socket.ReleaseSends();
		await published.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(Enumerable.Range(0, 512).Select(index => index.ToString()).Append("last"), socket.Messages);
		await stopping.CancelAsync();
		await serve;
	}

	[Fact]
	public async Task DisconnectReleasesAwaitedPublication() {
		var bridge = new WebSocketHostBridge();
		var socket = new StalledSendSocket();
		var serve = bridge.ServeAsync(socket, CancellationToken.None);
		var published = bridge.BroadcastAsync(Message("review", "payload"), CancellationToken.None);
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));
		socket.Abort();
		await published.WaitAsync(TimeSpan.FromSeconds(5));
		await serve.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task CancellationReleasesAwaitedPublication() {
		var bridge = new WebSocketHostBridge();
		var socket = new StalledSendSocket();
		using var cancellation = new CancellationTokenSource();
		var serve = bridge.ServeAsync(socket, CancellationToken.None);
		var published = bridge.BroadcastAsync(Message("review", "payload"), cancellation.Token);
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => published);
		socket.Abort();
		await serve.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static WebTransportMessage Message(string feature, string json) =>
		new(new WebMessageRoute(string.Empty, string.Empty, feature), json);

	// A WebSocket whose sends never complete until released, modelling a client that is connected and reading
	// but slower than the push rate. ReceiveAsync blocks until the socket is aborted so the read loop stays
	// alive throughout (the connection is not closing itself — the bridge drops it).
	private sealed class StalledSendSocket : WebSocket {
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly CancellationTokenSource _receiveGate = new();
		private int _sends;

		public List<string> Messages { get; } = [];

		public SemaphoreSlim FirstSendStarted { get; } = new(0);
		public SemaphoreSlim Aborted { get; } = new(0);
		public void ReleaseSends() => _release.TrySetResult();

		public override WebSocketState State => _receiveGate.IsCancellationRequested ? WebSocketState.Aborted : WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) {
			if (Interlocked.Increment(ref _sends) == 1) {
				FirstSendStarted.Release();
			}

			await _release.Task.WaitAsync(cancellationToken);
			Messages.Add(Encoding.UTF8.GetString(buffer));
		}

		public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) {
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _receiveGate.Token);
			await Task.Delay(Timeout.Infinite, linked.Token);
			throw new OperationCanceledException();
		}

		public override void Abort() {
			Aborted.Release();
			_receiveGate.Cancel();
			_release.TrySetResult();
		}

		public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
		public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
		public override void Dispose() => _receiveGate.Cancel();
	}
}
