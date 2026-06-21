using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Weavie.Core.Lsp;

/// <summary>
/// One live bridge session: a browser-side <see cref="WebSocket"/> (the <c>monaco-languageclient</c>) piped to
/// a spawned language server's stdio. A dumb proxy — each WebSocket text frame is one JSON-RPC message,
/// re-framed with <c>Content-Length</c> headers onto the server's stdin, and each <c>Content-Length</c> frame
/// from stdout is sent back as one WebSocket frame. Server stderr is forwarded to the log. Tearing down either
/// side tears down the other.
/// </summary>
internal sealed class LspConnection {
	private readonly WebSocket _socket;
	private readonly Process _process;
	private readonly string _label;
	private readonly Action<string> _log;
	private readonly SemaphoreSlim _stdinLock = new(1, 1);

	public LspConnection(WebSocket socket, Process process, string label, Action<string> log) {
		_socket = socket;
		_process = process;
		_label = label;
		_log = log;
	}

	/// <summary>Pumps both directions until the WebSocket closes or the server exits, then tears down.</summary>
	public async Task RunAsync(CancellationToken ct) {
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var token = linked.Token;

		// Each pump returns normally on every expected failure, so once one finishes we cancel and the rest
		// unwind cleanly.
		var pumps = new[] {
			PumpClientToServerAsync(token),
			PumpServerToClientAsync(token),
			PumpStderrAsync(token),
			WaitForExitAsync(token),
		};

		await Task.WhenAny(pumps).ConfigureAwait(false);
		await linked.CancelAsync().ConfigureAwait(false);
		await Task.WhenAll(pumps).ConfigureAwait(false);

		await TerminateProcessAsync().ConfigureAwait(false);
		await CloseSocketAsync().ConfigureAwait(false);
		_log($"{_label}: session ended");
	}

	private async Task PumpClientToServerAsync(CancellationToken ct) {
		byte[] buffer = new byte[64 * 1024];
		using var message = new MemoryStream();

		while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested) {
			WebSocketReceiveResult result;
			try {
				result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
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

			ReadOnlyMemory<byte> body = message.GetBuffer().AsMemory(0, (int)message.Length);
			try {
				await WriteToServerAsync(body, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
				break;
			}

			message.SetLength(0);
		}
	}

	/// <summary>
	/// Injects a host-originated JSON-RPC notification into the server's stdin (e.g.
	/// <c>workspace/didChangeWatchedFiles</c>). Shares the stdin write lock with the client pump so frames
	/// never interleave. Best-effort: no-ops if the server is gone.
	/// </summary>
	/// <param name="method">The notification method name.</param>
	/// <param name="paramsJson">The pre-serialized JSON for the notification's <c>params</c>.</param>
	/// <param name="ct">Cancellation token.</param>
	public async Task SendNotificationAsync(string method, string paramsJson, CancellationToken ct) {
		string envelope = $"{{\"jsonrpc\":\"2.0\",\"method\":\"{JsonEncodedText.Encode(method)}\",\"params\":{paramsJson}}}";
		try {
			await WriteToServerAsync(Encoding.UTF8.GetBytes(envelope), ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) {
			// Server already torn down — nothing to notify.
		}
	}

	private async Task WriteToServerAsync(ReadOnlyMemory<byte> body, CancellationToken ct) {
		await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
		try {
			await LspFraming.WriteFrameAsync(_process.StandardInput.BaseStream, body, ct).ConfigureAwait(false);
		} finally {
			_stdinLock.Release();
		}
	}

	private async Task PumpServerToClientAsync(CancellationToken ct) {
		var stdout = _process.StandardOutput.BaseStream;

		while (!ct.IsCancellationRequested) {
			byte[]? body;
			try {
				body = await LspFraming.ReadFrameAsync(stdout, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException) {
				break;
			}

			if (body is null) {
				break; // server closed stdout
			}

			try {
				await _socket.SendAsync(new ArraySegment<byte>(body), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) {
				break;
			}
		}
	}

	private async Task PumpStderrAsync(CancellationToken ct) {
		var stderr = _process.StandardError;
		try {
			while (await stderr.ReadLineAsync(ct).ConfigureAwait(false) is { } line) {
				if (line.Length > 0) {
					_log($"{_label} stderr: {line}");
				}
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) {
			// Server exited or stream torn down — nothing left to forward.
		}
	}

	private async Task WaitForExitAsync(CancellationToken ct) {
		try {
			await _process.WaitForExitAsync(ct).ConfigureAwait(false);
			_log($"{_label}: server exited with code {_process.ExitCode}");
		} catch (OperationCanceledException) {
			// Torn down by the other side; TerminateProcessAsync handles the kill.
		}
	}

	private async Task TerminateProcessAsync() {
		try {
			if (!_process.HasExited) {
				_process.Kill(entireProcessTree: true);
				using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception) {
			// Already gone, or refused to die in time — best effort.
		} finally {
			_process.Dispose();
		}
	}

	private async Task CloseSocketAsync() {
		if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
			try {
				await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException) {
				// Peer already gone.
			}
		}
	}
}
