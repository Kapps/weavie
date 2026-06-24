using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The <see cref="IHostBridge"/> for the headless host: the JS&lt;-&gt;C# bridge carried over a WebSocket so an
/// ordinary browser is the client. A worker can have more than one page connected at once (a second tab, or a
/// remote agent that loops back to the same worker), so every push is broadcast to <b>all</b> connections. Each
/// connection owns a bounded outbound queue drained by its own send loop, so one slow or dead peer can never
/// stall the others or grow memory without bound: a connection that falls <see cref="OutboxCapacity"/> messages
/// behind is dropped loudly. Pushes with no page connected are dropped, never buffered (each page re-requests
/// state on its <c>ready</c>).
/// </summary>
internal sealed class WebSocketHostBridge : IHostBridge {
	// A connection this many messages behind is treated as dead/hopeless and dropped — far above any healthy
	// burst (a loopback page drains in microseconds), low enough to bound memory and fail fast. A dropped page's
	// transport reconnects and re-requests state, so an over-eager drop self-heals rather than losing the page.
	private const int OutboxCapacity = 512;

	private readonly ConcurrentDictionary<Connection, byte> _connections = new();

	/// <inheritdoc/>
	public event Action<string>? MessageReceived;

	/// <inheritdoc/>
	public void PostToWeb(string json) {
		if (_connections.IsEmpty) {
			return; // No page connected; the page re-requests state on its next `ready`.
		}

		byte[] bytes = Encoding.UTF8.GetBytes(json);
		foreach (var connection in _connections.Keys) {
			// Non-blocking: a full queue means this client isn't draining (a dead/half-open peer, or one hopelessly
			// slow). Drop it so it can't stall the broadcast for the others — and never block the caller, which is
			// the UI / hook thread.
			if (!connection.Outbox.Writer.TryWrite(bytes)) {
				Drop(connection, "outbound queue full — page not keeping up");
			}
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
				MessageReceived?.Invoke(json);
			}
		} finally {
			_connections.TryRemove(connection, out _);
			connection.Outbox.Writer.TryComplete();
			// Abort so a send loop blocked on a dead peer unblocks at once; harmless on an already-closed socket.
			try {
				socket.Abort();
			} catch (ObjectDisposedException) {
			}

			await sendLoop.ConfigureAwait(false); // no send may race the caller's socket dispose
		}
	}

	/// <summary>
	/// Drains one connection's queue to its socket — the sole sender for that socket (WebSocket sends may not
	/// overlap, so exactly one send loop per socket). Ends when the queue is completed or the socket drops.
	/// </summary>
	private static async Task SendLoopAsync(Connection connection) {
		try {
			await foreach (byte[] bytes in connection.Outbox.Reader.ReadAllAsync().ConfigureAwait(false)) {
				if (connection.Socket.State != WebSocketState.Open) {
					break;
				}

				await connection.Socket
					.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
					.ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) {
			// The peer dropped mid-send; ServeAsync's finally (or a Drop) deregisters it. Stop sending.
		}
	}

	/// <summary>Forcibly removes a connection (dead or hopelessly slow) and aborts it so both its loops unwind.</summary>
	private void Drop(Connection connection, string reason) {
		if (_connections.TryRemove(connection, out _)) {
			connection.Outbox.Writer.TryComplete();
			Console.WriteLine($"[weavie-headless] dropped a page connection: {reason}");
			Console.Out.Flush();
		}

		// Unblocks a send loop stuck on a dead peer's full buffer and the read loop's ReceiveAsync; idempotent.
		try {
			connection.Socket.Abort();
		} catch (ObjectDisposedException) {
		}
	}

	/// <summary>One page connection: its socket plus a bounded outbound queue drained by a dedicated send loop.</summary>
	private sealed class Connection {
		public Connection(WebSocket socket) {
			Socket = socket;
			Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboxCapacity) {
				SingleReader = true,
				SingleWriter = false,
				// TryWrite returns false when full (it never blocks), which is PostToWeb's signal to drop the client.
				FullMode = BoundedChannelFullMode.Wait,
			});
		}

		public WebSocket Socket { get; }

		public Channel<byte[]> Outbox { get; }
	}
}
