using System.Net.WebSockets;
using System.Text;
using Weavie.Hosting;
using Xunit;

namespace Weavie.Headless.Tests;

public sealed class PageLifecycleTests {
	[Fact]
	public async Task ClosingAConnection_ReleasesItsPeer() {
		var bridge = new WebSocketHostBridge(new InlineUiDispatcher());
		WebPeer? disconnected = null;
		bridge.PeerDisconnected += peer => disconnected = peer;

		await bridge.ServeAsync(new MessageThenCloseSocket(), CancellationToken.None);

		Assert.NotNull(disconnected);
	}

	private sealed class MessageThenCloseSocket : WebSocket {
		private readonly byte[] _message = Encoding.UTF8.GetBytes(
			"""{"scope":"host","kind":"event","feature":"diagnostics","name":"log","payload":{"level":"info","message":"hi"}}""");
		private int _receiveCount;

		public override WebSocketState State { get; } = WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public override Task<WebSocketReceiveResult> ReceiveAsync(
			ArraySegment<byte> buffer,
			CancellationToken cancellationToken) {
			if (Interlocked.Increment(ref _receiveCount) == 1) {
				_message.AsSpan().CopyTo(buffer.AsSpan());
				return Task.FromResult(new WebSocketReceiveResult(
					_message.Length, WebSocketMessageType.Text, endOfMessage: true));
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
