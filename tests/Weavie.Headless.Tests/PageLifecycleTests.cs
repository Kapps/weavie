using System.Net.WebSockets;
using Weavie.Hosting;
using Xunit;
using ZstdSharp;

namespace Weavie.Headless.Tests;

public sealed class PageLifecycleTests {
	[Fact]
	public async Task ClosingAConnection_ReleasesItsPeer() {
		var bridge = new WebSocketHostBridge();
		WebPeer? disconnected = null;
		bridge.PeerDisconnected += peer => disconnected = peer;

		await bridge.ServeAsync(new MessageThenCloseSocket(TestWebSocketCodec.Encode("{}"), WebSocketMessageType.Binary), CancellationToken.None);

		Assert.NotNull(disconnected);
	}

	[Fact]
	public async Task CorruptFrameReleasesItsPeer() {
		var bridge = new WebSocketHostBridge();
		WebPeer? disconnected = null;
		bridge.PeerDisconnected += peer => disconnected = peer;
		await Assert.ThrowsAsync<ZstdException>(() => bridge.ServeAsync(
			new MessageThenCloseSocket([1, 2, 3], WebSocketMessageType.Binary), CancellationToken.None));
		Assert.NotNull(disconnected);
	}

	[Fact]
	public async Task TextFrameIsRejectedAndReleasesItsPeer() {
		var bridge = new WebSocketHostBridge();
		WebPeer? disconnected = null;
		bridge.PeerDisconnected += peer => disconnected = peer;
		await Assert.ThrowsAsync<InvalidDataException>(() => bridge.ServeAsync(
			new MessageThenCloseSocket("{}"u8.ToArray(), WebSocketMessageType.Text), CancellationToken.None));
		Assert.NotNull(disconnected);
	}

	private sealed class MessageThenCloseSocket(byte[] message, WebSocketMessageType type) : WebSocket {
		private int _receiveCount;

		public override WebSocketState State { get; } = WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public override Task<WebSocketReceiveResult> ReceiveAsync(
			ArraySegment<byte> buffer,
			CancellationToken cancellationToken) {
			if (Interlocked.Increment(ref _receiveCount) == 1) {
				message.AsSpan().CopyTo(buffer.AsSpan());
				return Task.FromResult(new WebSocketReceiveResult(
					message.Length, type, endOfMessage: true));
			}

			return Task.FromResult(new WebSocketReceiveResult(
				0, WebSocketMessageType.Close, endOfMessage: true));
		}

		public override Task SendAsync(
			ArraySegment<byte> buffer,
			WebSocketMessageType messageType,
			bool endOfMessage,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public override void Abort() {
		}

		public override Task CloseAsync(
			WebSocketCloseStatus closeStatus,
			string? statusDescription,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public override Task CloseOutputAsync(
			WebSocketCloseStatus closeStatus,
			string? statusDescription,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public override void Dispose() {
		}
	}
}
