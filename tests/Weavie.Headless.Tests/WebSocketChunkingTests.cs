using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Hosting;
using Xunit;

namespace Weavie.Headless.Tests;

public sealed class WebSocketChunkingTests {
	[Fact]
	public async Task LargeMessageUsesBoundedLogicalMessagesAndReassemblesExactly() {
		var bridge = new WebSocketHostBridge();
		var socket = new CapturingSocket();
		using var stopping = new CancellationTokenSource();
		var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bridge.MessageReceived += (_, _) => received.TrySetResult();
		var serving = bridge.ServeAsync(socket, stopping.Token);
		await received.Task;
		string json = JsonSerializer.Serialize(new { payload = new string('\u2603', 1_000_000) });

		bridge.Broadcast(Message("agent", json));

		var messages = await socket.Complete;
		Assert.True(messages.Count > 1);
		Assert.All(messages, message => Assert.InRange(message.Length, 1, WebSocketHostBridge.MaxWireMessageBytes));
		using var reassembled = new MemoryStream();
		for (int index = 0; index < messages.Count; index++) {
			using var document = JsonDocument.Parse(messages[index]);
			var chunk = document.RootElement.GetProperty("$weavieChunk");
			Assert.Equal(index, chunk.GetProperty("index").GetInt32());
			Assert.Equal(messages.Count, chunk.GetProperty("count").GetInt32());
			reassembled.Write(Convert.FromBase64String(chunk.GetProperty("data").GetString()!));
		}

		Assert.Equal(json, Encoding.UTF8.GetString(reassembled.ToArray()));
		await stopping.CancelAsync();
		await serving;
	}

	[Fact]
	public async Task SmallMessageRemainsOneLogicalMessage() {
		var bridge = new WebSocketHostBridge();
		var socket = new CapturingSocket();
		using var stopping = new CancellationTokenSource();
		var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bridge.MessageReceived += (_, _) => received.TrySetResult();
		var serving = bridge.ServeAsync(socket, stopping.Token);
		await received.Task;
		string json = JsonSerializer.Serialize(new { payload = "small" });

		bridge.Broadcast(Message("agent", json));

		byte[] message = Assert.Single(await socket.Complete);
		Assert.Equal(json, Encoding.UTF8.GetString(message));
		await stopping.CancelAsync();
		await serving;
	}

	[Fact]
	public async Task UnrelatedRouteCanPassAChunkedMessage() {
		var bridge = new WebSocketHostBridge();
		var socket = new GatedCapturingSocket();
		using var stopping = new CancellationTokenSource();
		var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bridge.MessageReceived += (_, _) => received.TrySetResult();
		var serving = bridge.ServeAsync(socket, stopping.Token);
		await received.Task;
		string large = JsonSerializer.Serialize(new { payload = new string('a', 1_000_000) });
		string branch = JsonSerializer.Serialize(new { branches = new[] { "main" } });

		bridge.Broadcast(Message("agent", large));
		await socket.FirstSendStarted;
		int chunkCount = ChunkCount(socket.FirstMessage);
		bridge.Broadcast(Message("git", branch));
		socket.Expect(chunkCount + 1);
		socket.ReleaseFirstSend();

		var messages = await socket.Complete;
		Assert.Equal(branch, Encoding.UTF8.GetString(messages[1]));
		Assert.All(messages.Where((_, index) => index != 1), message => Assert.True(IsChunk(message)));
		await stopping.CancelAsync();
		await serving;
	}

	[Fact]
	public async Task SameRoutePreservesLogicalMessageOrder() {
		var bridge = new WebSocketHostBridge();
		var socket = new GatedCapturingSocket();
		using var stopping = new CancellationTokenSource();
		var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bridge.MessageReceived += (_, _) => received.TrySetResult();
		var serving = bridge.ServeAsync(socket, stopping.Token);
		await received.Task;
		string large = JsonSerializer.Serialize(new { payload = new string('a', 1_000_000) });
		string control = JsonSerializer.Serialize(new { state = "idle" });

		bridge.Broadcast(Message("agent", large));
		await socket.FirstSendStarted;
		int chunkCount = ChunkCount(socket.FirstMessage);
		bridge.Broadcast(Message("agent", control));
		socket.Expect(chunkCount + 1);
		socket.ReleaseFirstSend();

		var messages = await socket.Complete;
		Assert.All(messages.Take(chunkCount), message => Assert.True(IsChunk(message)));
		Assert.Equal(control, Encoding.UTF8.GetString(messages[^1]));
		await stopping.CancelAsync();
		await serving;
	}

	[Fact]
	public async Task DeepSameRouteBacklogCannotHideAnUnrelatedRoute() {
		var bridge = new WebSocketHostBridge();
		var socket = new GatedCapturingSocket();
		using var stopping = new CancellationTokenSource();
		var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bridge.MessageReceived += (_, _) => received.TrySetResult();
		var serving = bridge.ServeAsync(socket, stopping.Token);
		await received.Task;

		bridge.Broadcast(Message("agent", "{\"agent\":-1}"));
		await socket.FirstSendStarted;
		for (int index = 0; index < 500; index++) {
			bridge.Broadcast(Message("agent", $"{{\"agent\":{index}}}"));
		}
		const string branch = "{\"branches\":[\"main\"]}";
		bridge.Broadcast(Message("git", branch));
		socket.Expect(502);
		socket.ReleaseFirstSend();

		var messages = await socket.Complete;
		Assert.Equal(branch, Encoding.UTF8.GetString(messages[1]));
		await stopping.CancelAsync();
		await serving;
	}

	private static int ChunkCount(byte[] message) {
		using var document = JsonDocument.Parse(message);
		return document.RootElement.GetProperty("$weavieChunk").GetProperty("count").GetInt32();
	}

	private static bool IsChunk(byte[] message) {
		using var document = JsonDocument.Parse(message);
		return document.RootElement.TryGetProperty("$weavieChunk", out _);
	}

	private static WebTransportMessage Message(string feature, string json) =>
		new(new WebMessageRoute(string.Empty, string.Empty, feature), json);

	private sealed class CapturingSocket : WebSocket {
		private readonly byte[] _hello = "{}"u8.ToArray();
		private readonly TaskCompletionSource<IReadOnlyList<byte[]>> _complete =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly List<byte[]> _messages = [];
		private bool _helloSent;
		private int _expected;

		public Task<IReadOnlyList<byte[]>> Complete => _complete.Task;

		public override WebSocketState State => WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public override async Task<WebSocketReceiveResult> ReceiveAsync(
			ArraySegment<byte> buffer,
			CancellationToken cancellationToken) {
			if (!_helloSent) {
				_helloSent = true;
				_hello.CopyTo(buffer.Array!, buffer.Offset);
				return new WebSocketReceiveResult(_hello.Length, WebSocketMessageType.Text, endOfMessage: true);
			}

			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
		}

		public override Task SendAsync(
			ArraySegment<byte> buffer,
			WebSocketMessageType messageType,
			bool endOfMessage,
			CancellationToken cancellationToken) {
			Assert.True(endOfMessage);
			byte[] message = [.. buffer];
			lock (_messages) {
				_messages.Add(message);
				if (_expected == 0) {
					using var document = JsonDocument.Parse(message);
					_expected = document.RootElement.TryGetProperty("$weavieChunk", out var chunk)
						? chunk.GetProperty("count").GetInt32()
						: 1;
				}
				if (_messages.Count == _expected) {
					_complete.TrySetResult(_messages.ToArray());
				}
			}

			return Task.CompletedTask;
		}

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

	private sealed class GatedCapturingSocket : WebSocket {
		private readonly byte[] _hello = "{}"u8.ToArray();
		private readonly TaskCompletionSource _firstSendStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _releaseFirstSend =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource<IReadOnlyList<byte[]>> _complete =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly List<byte[]> _messages = [];
		private bool _helloSent;
		private int _expected;

		public Task FirstSendStarted => _firstSendStarted.Task;
		public byte[] FirstMessage => _messages[0];
		public Task<IReadOnlyList<byte[]>> Complete => _complete.Task;

		public override WebSocketState State => WebSocketState.Open;
		public override WebSocketCloseStatus? CloseStatus => null;
		public override string? CloseStatusDescription => null;
		public override string? SubProtocol => null;

		public void Expect(int expected) {
			lock (_messages) {
				_expected = expected;
				TryComplete();
			}
		}

		public void ReleaseFirstSend() => _releaseFirstSend.TrySetResult();

		public override async Task<WebSocketReceiveResult> ReceiveAsync(
			ArraySegment<byte> buffer,
			CancellationToken cancellationToken) {
			if (!_helloSent) {
				_helloSent = true;
				_hello.CopyTo(buffer.Array!, buffer.Offset);
				return new WebSocketReceiveResult(_hello.Length, WebSocketMessageType.Text, endOfMessage: true);
			}

			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
		}

		public override async Task SendAsync(
			ArraySegment<byte> buffer,
			WebSocketMessageType messageType,
			bool endOfMessage,
			CancellationToken cancellationToken) {
			Assert.True(endOfMessage);
			bool first;
			lock (_messages) {
				first = _messages.Count == 0;
				_messages.Add([.. buffer]);
				TryComplete();
			}

			if (first) {
				_firstSendStarted.TrySetResult();
				await _releaseFirstSend.Task.WaitAsync(cancellationToken);
			}
		}

		public override void Abort() => _releaseFirstSend.TrySetResult();

		public override Task CloseAsync(
			WebSocketCloseStatus closeStatus,
			string? statusDescription,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public override Task CloseOutputAsync(
			WebSocketCloseStatus closeStatus,
			string? statusDescription,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public override void Dispose() => _releaseFirstSend.TrySetResult();

		private void TryComplete() {
			if (_expected > 0 && _messages.Count == _expected) {
				_complete.TrySetResult(_messages.ToArray());
			}
		}
	}
}
