using System.Net.WebSockets;
using System.Text;
using Weavie.Hosting;
using Xunit;

namespace Weavie.Headless.Tests;

public sealed class PageLifecycleTests {
	[Fact]
	public async Task ClosingAnAcquiredConnection_ReleasesItsPageIdentity() {
		var bridge = new WebSocketHostBridge(new InlineUiDispatcher());
		string? disconnected = null;
		bridge.PageDisconnected += pageId => disconnected = pageId;

		await bridge.ServeAsync(new AcquireThenCloseSocket("page-one"), CancellationToken.None);

		Assert.Equal("page-one", disconnected);
	}

	private sealed class AcquireThenCloseSocket(string pageId) : WebSocket {
		private readonly byte[] _acquire = Encoding.UTF8.GetBytes($$"""{"type":"acquire-editor","pageId":"{{pageId}}"}""");
		private int _receiveCount;

		public override WebSocketState State { get; } = WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public override Task<WebSocketReceiveResult> ReceiveAsync(
			ArraySegment<byte> buffer,
			CancellationToken cancellationToken) {
			if (Interlocked.Increment(ref _receiveCount) == 1) {
				_acquire.AsSpan().CopyTo(buffer.AsSpan());
				return Task.FromResult(new WebSocketReceiveResult(
					_acquire.Length, WebSocketMessageType.Text, endOfMessage: true));
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
