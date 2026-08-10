using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Weavie.Hosting;
using Weavie.Hosting.Web;

namespace Weavie.Headless;

/// <summary>
/// The <see cref="IWebTransportHub"/> for the headless host: the JS&lt;-&gt;C# bridge carried over a WebSocket so an
/// ordinary browser is the client. A worker can have more than one page connected at once (a second tab, or a
/// remote agent that loops back to the same worker), so every push is broadcast to <b>all</b> connections. Each
/// connection owns one bounded route-aware outbox, so one slow or dead peer can never stall the others or grow
/// memory without bound. A connection that fills that outbox is dropped loudly.
/// Pushes with no page connected are dropped, never buffered (each page requests fresh state when it connects).
/// </summary>
internal sealed class WebSocketHostBridge : IWebTransportHub, IWorkspaceWebSocketBridge {
	// A connection this many messages behind is treated as dead/hopeless and dropped — far above any healthy
	// burst (a loopback page drains in microseconds), low enough to bound memory and fail fast. A dropped page's
	// transport reconnects and re-requests state, so an over-eager drop self-heals rather than losing the page.
	private const int OutboxCapacity = 512;
	internal const int MaxWireMessageBytes = 768 * 1024;
	private const int ChunkPayloadBytes = 64 * 1024;
	private static readonly JsonSerializerOptions ChunkJsonOptions = new(JsonSerializerDefaults.Web);

	private readonly ConcurrentDictionary<Connection, byte> _connections = new();
	private long _chunkSequence;

	/// <inheritdoc/>
	public event Action<WebPeer, string>? MessageReceived;

	/// <inheritdoc/>
	public event Action<WebPeer>? PeerDisconnected;

	/// <inheritdoc/>
	public bool Available => true;

	/// <inheritdoc/>
	public void Broadcast(WebTransportMessage message) {
		if (_connections.IsEmpty) {
			return; // No page connected; the next connection requests fresh state.
		}

		var outbound = Encode(message);
		foreach (var connection in _connections.Keys) {
			// Non-blocking: a full queue means this client isn't draining (a dead/half-open peer, or one hopelessly
			// slow). Drop it so it can't stall the broadcast for the others — and never block the caller, which is
			// the UI / hook thread.
			if (!connection.Outbox.TryWrite(outbound)) {
				Drop(connection, "outbound queue full — page not keeping up");
			}
		}
	}

	/// <inheritdoc/>
	public void Send(WebPeer peer, WebTransportMessage message) {
		var connection = _connections.Keys.FirstOrDefault(candidate => candidate.Peer == peer);
		if (connection is null) {
			return;
		}

		if (!connection.Outbox.TryWrite(Encode(message))) {
			Drop(connection, "outbound queue full — page not keeping up");
		}
	}

	/// <summary>
	/// Drives one page connection: registers it, starts its dedicated send loop, then reads frames until it
	/// disconnects, raising <see cref="MessageReceived"/> for each complete text message. On exit it deregisters
	/// the connection and winds the send loop down before the caller disposes the socket.
	/// </summary>
	public async Task ServeAsync(WebSocket socket, CancellationToken cancellationToken) {
		var connection = new Connection(socket);
		_connections.TryAdd(connection, 0);
		var sendLoop = SendLoopAsync(connection);
		byte[] buffer = new byte[64 * 1024];
		var message = new MemoryStream();
		try {
			while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested) {
				WebSocketReceiveResult result;
				try {
					result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
				} catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) {
					break;
				}

				if (result.MessageType == WebSocketMessageType.Close) {
					break;
				}

				message.Write(buffer, 0, result.Count);
				if (!result.EndOfMessage) {
					continue;
				}

				string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
				message.SetLength(0);
				MessageReceived?.Invoke(connection.Peer, json);
			}
		} finally {
			_connections.TryRemove(connection, out _);
			connection.Outbox.Complete();
			// Abort so a send loop blocked on a dead peer unblocks at once; harmless on an already-closed socket.
			try {
				socket.Abort();
			} catch (ObjectDisposedException) {
			}

			await sendLoop.ConfigureAwait(false); // no send may race the caller's socket dispose
			PeerDisconnected?.Invoke(connection.Peer);
		}
	}

	/// <summary>
	/// Drains one connection's queue to its socket — the sole sender for that socket (WebSocket sends may not
	/// overlap, so exactly one send loop per socket). Ends when the queue is completed or the socket drops.
	/// </summary>
	private static async Task SendLoopAsync(Connection connection) {
		try {
			while (await connection.Outbox.NextAsync().ConfigureAwait(false) is { } turn) {
				if (connection.Socket.State != WebSocketState.Open) {
					break;
				}

				await connection.Socket
					.SendAsync(
						turn.Bytes,
						WebSocketMessageType.Text,
						endOfMessage: true,
						CancellationToken.None)
					.ConfigureAwait(false);
				connection.Outbox.CompleteTurn(turn);
			}
		} catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) {
			// The peer dropped mid-send; ServeAsync's finally (or a Drop) deregisters it. Stop sending.
		}
	}

	private OutboundMessage Encode(WebTransportMessage message) {
		byte[] bytes = Encoding.UTF8.GetBytes(message.Json);
		if (bytes.Length <= MaxWireMessageBytes) {
			return new OutboundMessage(message.Route, [bytes]);
		}

		string id = Interlocked.Increment(ref _chunkSequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
		int count = (bytes.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
		byte[][] messages = new byte[count][];
		for (int index = 0; index < count; index++) {
			int offset = index * ChunkPayloadBytes;
			int length = Math.Min(ChunkPayloadBytes, bytes.Length - offset);
			messages[index] = JsonSerializer.SerializeToUtf8Bytes(new ChunkWire(new ChunkBody(
				id,
				index,
				count,
				Convert.ToBase64String(bytes, offset, length))), ChunkJsonOptions);
		}

		return new OutboundMessage(message.Route, messages);
	}

	/// <summary>Forcibly removes a connection (dead or hopelessly slow) and aborts it so both its loops unwind.</summary>
	private void Drop(Connection connection, string reason) {
		if (_connections.TryRemove(connection, out _)) {
			connection.Outbox.Complete();
			Console.WriteLine($"[weavie-headless] dropped a page connection: {reason}");
			Console.Out.Flush();
		}

		// Unblocks a send loop stuck on a dead peer's full buffer and the read loop's ReceiveAsync; idempotent.
		try {
			connection.Socket.Abort();
		} catch (ObjectDisposedException) {
		}
	}

	/// <summary>One page connection: its socket plus a bounded fair outbox drained by one send loop.</summary>
	private sealed class Connection {
		public Connection(WebSocket socket) {
			Socket = socket;
			Peer = new WebPeer(Guid.NewGuid().ToString("n"));
			Outbox = new FairOutbox(OutboxCapacity);
		}

		public WebSocket Socket { get; }

		public WebPeer Peer { get; }

		public FairOutbox Outbox { get; }
	}

	private sealed record OutboundMessage(WebMessageRoute Route, IReadOnlyList<byte[]> Messages);

	private sealed class PendingMessage(OutboundMessage message) {
		private int _index;

		public bool Complete => _index == message.Messages.Count;

		public byte[] Next() => message.Messages[_index++];
	}

	private sealed class FairOutbox(int capacity) {
		private readonly object _gate = new();
		private readonly Dictionary<WebMessageRoute, Queue<PendingMessage>> _pending = [];
		private readonly Queue<WebMessageRoute> _routes = [];
		private TaskCompletionSource _changed = NewSignal();
		private bool _closed;
		private int _messages;

		public bool TryWrite(OutboundMessage message) {
			lock (_gate) {
				if (_closed || _messages >= capacity) {
					return false;
				}
				bool addedRoute = false;
				if (!_pending.TryGetValue(message.Route, out var queue)) {
					queue = new Queue<PendingMessage>();
					_pending.Add(message.Route, queue);
					_routes.Enqueue(message.Route);
					addedRoute = true;
				}
				queue.Enqueue(new PendingMessage(message));
				_messages++;
				if (addedRoute) {
					PulseLocked();
				}
				return true;
			}
		}

		public async ValueTask<OutboundTurn?> NextAsync() {
			while (true) {
				Task changed;
				lock (_gate) {
					if (_routes.TryDequeue(out var route)) {
						var message = _pending[route].Peek();
						byte[] bytes = message.Next();
						return new OutboundTurn(route, bytes, message.Complete);
					}
					if (_closed) {
						return null;
					}
					changed = _changed.Task;
				}
				await changed.ConfigureAwait(false);
			}
		}

		public void CompleteTurn(OutboundTurn turn) {
			lock (_gate) {
				var queue = _pending[turn.Route];
				if (turn.CompletesMessage) {
					queue.Dequeue();
					_messages--;
				}
				if (queue.Count == 0) {
					_pending.Remove(turn.Route);
				} else {
					_routes.Enqueue(turn.Route);
				}
				PulseLocked();
			}
		}

		public void Complete() {
			lock (_gate) {
				_closed = true;
				PulseLocked();
			}
		}

		private void PulseLocked() {
			var changed = _changed;
			_changed = NewSignal();
			changed.TrySetResult();
		}

		private static TaskCompletionSource NewSignal() =>
			new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	private sealed record OutboundTurn(
		WebMessageRoute Route,
		byte[] Bytes,
		bool CompletesMessage);

	private sealed record ChunkWire(
		[property: JsonPropertyName("$weavieChunk")] ChunkBody Chunk);

	private sealed record ChunkBody(string Id, int Index, int Count, string Data);
}
