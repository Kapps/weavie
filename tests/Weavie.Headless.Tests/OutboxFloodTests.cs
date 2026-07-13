using System.Net.WebSockets;
using Weavie.Hosting;
using Xunit;

namespace Weavie.Headless.Tests;

// Reproduces the remote-only "stuck connecting" bug: a HEALTHY but network-slow page connection is dropped
// when a synchronous push burst exceeds the bridge's bounded outbox. On loopback the send loop drains in
// microseconds so this never fires; over a real WSS link (or here, a deliberately stalled send) a large
// replay burst (ReplayPane posts one frame per transcript entry) fills the 512-deep outbox faster than it
// drains, and the worker aborts a client that is very much alive — which then reconnects and floods again.
public sealed class OutboxFloodTests {
	[Fact]
	public async Task A_burst_larger_than_the_outbox_drops_a_slow_but_alive_connection() {
		var bridge = new WebSocketHostBridge(new InlineUiDispatcher());
		var socket = new StalledSendSocket();
		var serve = bridge.ServeAsync(socket, CancellationToken.None);

		// Prime one frame and wait for the send loop to dequeue it and stall inside SendAsync (a slow network):
		// now the outbox drains nothing, exactly as a wedged-slow remote link would between sends.
		bridge.PostToWeb("{\"type\":\"agent-pane\",\"n\":-1}");
		Assert.True(await socket.FirstSendStarted.WaitAsync(TimeSpan.FromSeconds(5)));

		// The stalled frame is in-flight; the outbox itself now holds 512. Push past that in a tight, await-free
		// loop — exactly how ReplayPane posted a long transcript on `ready`, one frame per entry.
		for (int i = 0; i < 600; i++) {
			bridge.PostToWeb($"{{\"type\":\"agent-pane\",\"n\":{i}}}");
		}

		// The bridge treated the backlog as a dead peer and aborted the socket — even though it is alive and
		// its send loop is still making progress. This abort is what surfaces client-side as the endless
		// reconnect loop.
		Assert.True(await socket.Aborted.WaitAsync(TimeSpan.FromSeconds(5)));

		socket.ReleaseSends();
		await serve.WaitAsync(TimeSpan.FromSeconds(5));
	}

	// A WebSocket whose sends never complete until released, modelling a client that is connected and reading
	// but slower than the push rate. ReceiveAsync blocks until the socket is aborted so the read loop stays
	// alive throughout (the connection is not closing itself — the bridge drops it).
	private sealed class StalledSendSocket : WebSocket {
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly CancellationTokenSource _receiveGate = new();
		private int _sends;

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
