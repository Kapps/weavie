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
	public async Task CompressedLargeBurstFitsWhileOnePeerIsStalled() {
		var bridge = new WebSocketHostBridge();
		var slow = new StalledSendSocket();
		var healthy = new StalledSendSocket();
		healthy.ReleaseSends();
		var slowServe = bridge.ServeAsync(slow, CancellationToken.None);
		var healthyServe = bridge.ServeAsync(healthy, CancellationToken.None);
		const int count = 30;
		string payload = new('a', 700_000);
		try {
			for (int index = 0; index < count; index++) {
				bridge.Broadcast(new WebTransportMessage(new WebMessageRoute("", "", "review"), payload));
			}
			bridge.Broadcast(new WebTransportMessage(new WebMessageRoute("", "", "review"), "done"));
			await healthy.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(WebSocketState.Open, slow.State);
			Assert.False(slow.Done.Task.IsCompleted);
			Assert.Equal(count, healthy.Messages.Where(json => json != "done").Select(json => {
				using var document = JsonDocument.Parse(json);
				return document.RootElement.GetProperty("$weavieChunk").GetProperty("id").GetString();
			}).Distinct().Count());
			Assert.True(healthy.WireBytes < count * payload.Length / 10);
			slow.ReleaseSends();
			await slow.Done.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(healthy.Messages.ToArray(), slow.Messages.ToArray());
		} finally {
			slow.Abort();
			healthy.Abort();
			await Task.WhenAll(slowServe, healthyServe);
		}
	}

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
		public int WireBytes { get; private set; }
		public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
			string json = Encoding.UTF8.GetString(TestWebSocketCodec.Decode(buffer.AsSpan()));
			Messages.Add(json);
			WireBytes += buffer.Count;
			if (json == "done") Done.TrySetResult();
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
