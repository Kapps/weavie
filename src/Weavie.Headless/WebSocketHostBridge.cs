using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The <see cref="IHostBridge"/> for the headless host: the same JS&lt;-&gt;C# bridge the native shells run
/// over their web view, carried instead over a WebSocket so an ordinary browser is the client. Outbound
/// <see cref="PostToWeb"/> sends are funneled through a single channel + pump task (WebSocket sends may not
/// overlap); inbound text frames are reassembled and raised as <see cref="MessageReceived"/>. The bridge
/// outlives any one connection — like the native hosts, the host persists while the page reloads — so a
/// browser refresh just calls <see cref="ServeAsync"/> again with a fresh socket and the page re-sends
/// <c>ready</c>. Pushes made while no page is connected are dropped (the page re-requests state on
/// <c>ready</c>), never buffered indefinitely.
/// </summary>
internal sealed class WebSocketHostBridge : IHostBridge {
	private readonly Channel<string> _outbound =
		Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

	private volatile WebSocket? _socket;

	/// <summary>Starts the outbound pump that owns all sends to the current socket.</summary>
	public WebSocketHostBridge() {
		_ = PumpAsync();
	}

	/// <inheritdoc/>
	public event Action<string>? MessageReceived;

	/// <inheritdoc/>
	public void PostToWeb(string json) => _outbound.Writer.TryWrite(json);

	/// <summary>
	/// Drives one page connection: makes the socket current, then reads frames until the page disconnects,
	/// raising <see cref="MessageReceived"/> for each complete text message. Returns when the socket closes.
	/// </summary>
	public async Task ServeAsync(WebSocket socket, CancellationToken cancellationToken) {
		_socket = socket;
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
			if (ReferenceEquals(_socket, socket)) {
				_socket = null;
			}
		}
	}

	private async Task PumpAsync() {
		await foreach (string json in _outbound.Reader.ReadAllAsync().ConfigureAwait(false)) {
			var socket = _socket;
			if (socket is not { State: WebSocketState.Open }) {
				continue; // No page connected; the page re-requests state on its next `ready`.
			}

			try {
				await socket
					.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
					.ConfigureAwait(false);
			} catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) {
				// The socket dropped mid-send; the next ServeAsync will re-establish. Drop this frame.
			}
		}
	}
}
