using System.Diagnostics;
using System.Threading.Channels;

namespace Weavie.Core.Lsp;

/// <summary>
/// An <see cref="ILspServerProcess"/> over a real <see cref="Process"/>: pumps stdout frames out as
/// <see cref="FrameReceived"/>, drains a single-consumer queue onto stdin in submission order, forwards stderr to
/// the log, and raises <see cref="Exited"/> once the process ends. The de-stdio'd successor to the old
/// WebSocket-coupled bridge connection — it knows nothing about how frames reach the editor.
/// </summary>
internal sealed class LspServerProcess : ILspServerProcess {
	private readonly Process _process;
	private readonly string _label;
	private readonly Action<string> _log;
	private readonly CancellationTokenSource _cts = new();
	// Single-reader outbound queue: callers enqueue from any thread; one pump frames them to stdin in order, so
	// LSP message ordering is preserved (a SemaphoreSlim around stdin would not guarantee FIFO).
	private readonly Channel<byte[]> _outbound =
		Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
	private int _exitedRaised;
	private bool _disposed;

	public LspServerProcess(Process process, string label, Action<string> log) {
		ArgumentNullException.ThrowIfNull(process);
		ArgumentNullException.ThrowIfNull(log);
		_process = process;
		_label = label;
		_log = log;
	}

	/// <inheritdoc/>
	public event Action<byte[]>? FrameReceived;

	/// <inheritdoc/>
	public event Action<int>? Exited;

	/// <inheritdoc/>
	public void Start() {
		_ = PumpStdoutAsync(_cts.Token);
		_ = PumpStderrAsync(_cts.Token);
		_ = PumpStdinAsync(_cts.Token);
		_ = WaitForExitAsync(_cts.Token);
	}

	/// <inheritdoc/>
	public void Write(ReadOnlyMemory<byte> payload) => _outbound.Writer.TryWrite(payload.ToArray());

	private async Task PumpStdoutAsync(CancellationToken ct) {
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

			FrameReceived?.Invoke(body);
		}
	}

	private async Task PumpStdinAsync(CancellationToken ct) {
		var stdin = _process.StandardInput.BaseStream;
		try {
			await foreach (byte[] payload in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
				await LspFraming.WriteFrameAsync(stdin, payload, ct).ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) {
			// Server stdin closed / torn down — nothing more to write.
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
		} catch (OperationCanceledException) {
			return; // disposed before a natural exit; Dispose does the reaping
		}

		RaiseExited(_process.HasExited ? _process.ExitCode : -1);
	}

	private void RaiseExited(int code) {
		if (Interlocked.Exchange(ref _exitedRaised, 1) == 0) {
			Exited?.Invoke(code);
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		if (_disposed) {
			return;
		}

		_disposed = true;
		_outbound.Writer.TryComplete();
		_cts.Cancel();
		try {
			if (!_process.HasExited) {
				// Force-kill the whole tree and wait — unbounded on purpose. The session's "no server outlives me"
				// guarantee (so a following worktree removal can't race a live process) only holds if we actually
				// block until it's gone; a bounded wait that gave up would be a silent fallback. A killed process exits.
				_process.Kill(entireProcessTree: true);
				_process.WaitForExit();
			}
		} catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
			// Already gone.
		} finally {
			_process.Dispose();
			_cts.Dispose();
		}
	}
}
