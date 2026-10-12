using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Weavie.Core.Processes;

namespace Weavie.Core.Lsp;

/// <summary>
/// An <see cref="ILspServerProcess"/> over a real <see cref="Process"/>: pumps stdout frames out as
/// <see cref="FrameReceived"/>, drains a single-consumer queue onto stdin in submission order, forwards stderr to
/// the log, and raises <see cref="Exited"/> once the process ends. The de-stdio'd successor to the old
/// WebSocket-coupled bridge connection — it knows nothing about how frames reach the editor.
/// </summary>
internal sealed class LspServerProcess : ILspServerProcess {
	private const string ShutdownId = "weavie/shutdown";
	private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(5);
	private static readonly byte[] ShutdownRequest = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":\"{ShutdownId}\",\"method\":\"shutdown\"}}");
	private static readonly byte[] ExitNotification = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
	private readonly OwnedProcess _process;
	private readonly string _label;
	private readonly Action<string> _log;
	private readonly CancellationTokenSource _cts = new();
	// Single-reader outbound queue: callers enqueue from any thread; one pump frames them to stdin in order, so
	// LSP message ordering is preserved (a SemaphoreSlim around stdin would not guarantee FIFO).
	private readonly Channel<byte[]> _outbound =
		Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
	private readonly TaskCompletionSource _shutdownAnswered = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task _stdinPump = Task.CompletedTask;
	private int _exitedRaised;
	private volatile bool _stopping;

	public LspServerProcess(OwnedProcess process, string label, Action<string> log) {
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
		_stdinPump = PumpStdinAsync(_cts.Token);
		_ = WaitForExitAsync(_cts.Token);
	}

	/// <inheritdoc/>
	public void Write(ReadOnlyMemory<byte> payload) {
		if (!_stopping) {
			_outbound.Writer.TryWrite(payload.ToArray());
		}
	}

	/// <inheritdoc/>
	public async Task<ProcessStopOutcome> StopAsync() {
		_stopping = true;
		var plan = ProcessStopPlan.Request("the LSP shutdown request", AskToExitAsync, StopDeadline);
		try {
			return await _process.StopAsync(plan, new SystemSupervisorClock()).ConfigureAwait(false);
		} finally {
			_outbound.Writer.TryComplete();
			await _cts.CancelAsync().ConfigureAwait(false);
			_process.Dispose();
			_cts.Dispose();
		}
	}

	// The LSP exit sequence: shutdown (answered, or refused by a server the page already shut down), exit, stdin EOF.
	private async Task AskToExitAsync(CancellationToken ct) {
		_outbound.Writer.TryWrite(ShutdownRequest);
		await _shutdownAnswered.Task.WaitAsync(ct).ConfigureAwait(false);
		_outbound.Writer.TryWrite(ExitNotification);
		_outbound.Writer.TryComplete();
		await _stdinPump.WaitAsync(ct).ConfigureAwait(false);
		_process.StandardInput.Close();
	}

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

			if (_stopping) {
				if (AnswersShutdown(body)) {
					_shutdownAnswered.TrySetResult();
				}
				continue;
			}

			try {
				FrameReceived?.Invoke(body);
			} catch (Exception ex) {
				// A subscriber fault must not kill the pump — that would silently freeze language features.
				_log($"{_label} frame handler threw: {ex.Message}");
			}
		}
	}

	private static bool AnswersShutdown(byte[] body) {
		try {
			using var document = JsonDocument.Parse(body);
			var root = document.RootElement;
			return root.ValueKind == JsonValueKind.Object
				&& !root.TryGetProperty("method", out _)
				&& root.TryGetProperty("id", out var id)
				&& id.ValueKind == JsonValueKind.String
				&& id.GetString() == ShutdownId;
		} catch (JsonException) {
			return false;
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
			return; // stopped before a natural exit; StopAsync does the reaping
		}

		RaiseExited(_process.HasExited ? _process.ExitCode : -1);
	}

	private void RaiseExited(int code) {
		if (Interlocked.Exchange(ref _exitedRaised, 1) == 0) {
			Exited?.Invoke(code);
		}
	}
}
