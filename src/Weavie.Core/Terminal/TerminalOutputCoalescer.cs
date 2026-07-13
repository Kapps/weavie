namespace Weavie.Core.Terminal;

/// <summary>
/// Batches a terminal pane's live PTY output into fewer, larger frames before it crosses the bridge to the page.
/// One frame per PTY read chunk overwhelms the headless bridge's bounded outbox on a burst — a page can't drain
/// ~15k tiny frames as fast as a flood produces them, so the connection is dropped and the UI freezes. Output
/// accumulates here and is emitted as one frame when it reaches a byte threshold or a short time window elapses,
/// whichever comes first, cutting a 38&#160;MB burst from ~15000 frames to ~150. Only the live post is deferred —
/// scrollback logging stays immediate. A window of 0 posts each chunk inline (no batching).
/// <para>Posts run under the lock, so output order is preserved across the PTY, timer, and flush-boundary
/// threads. The post callback must be non-blocking (the bridge's <c>PostToWeb</c> is), the same contract the
/// caller already relied on when posting inline.</para>
/// </summary>
public sealed class TerminalOutputCoalescer : IDisposable {
	// A burst flushes on this many buffered bytes, keeping frames-per-burst far under the outbox's 512-message
	// cap (a 38MB burst → ~150 frames) without inflating any single frame's base64/WebSocket cost.
	private const int FlushThresholdBytes = 256 * 1024;

	private readonly Action<byte[]> _post;
	private readonly long _windowMs;
	private readonly Lock _gate = new();
	private readonly Timer? _timer;
	private readonly MemoryStream _buffer = new();
	private bool _armed;
	private bool _disposed;

	/// <summary>
	/// Coalesces output posted through <paramref name="post"/> (the bridge sink), flushing after
	/// <paramref name="windowMs"/> milliseconds or the byte threshold. <paramref name="windowMs"/> of 0 posts
	/// each chunk inline.
	/// </summary>
	public TerminalOutputCoalescer(Action<byte[]> post, long windowMs) {
		ArgumentNullException.ThrowIfNull(post);
		_post = post;
		_windowMs = windowMs;
		if (windowMs > 0) {
			_timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
		}
	}

	/// <summary>Buffers one live output chunk, flushing inline when the window is disabled or the buffer is now
	/// full; otherwise arms the time-window flush. Runs on the PTY read thread; never blocks it.</summary>
	public void Add(ReadOnlySpan<byte> data) {
		if (data.IsEmpty) {
			return;
		}

		lock (_gate) {
			if (_disposed) {
				return;
			}

			if (_windowMs <= 0) {
				_post(data.ToArray());
				return;
			}

			_buffer.Write(data);
			if (_buffer.Length >= FlushThresholdBytes) {
				FlushLocked();
			} else if (!_armed) {
				_armed = true;
				_timer!.Change(_windowMs, Timeout.Infinite);
			}
		}
	}

	/// <summary>Emits any buffered output as one frame now — call before an exit marker or restore preamble so
	/// buffered output keeps its place ahead of them. No-op when empty.</summary>
	public void Flush() {
		lock (_gate) {
			if (!_disposed) {
				FlushLocked();
			}
		}
	}

	/// <summary>Drops buffered output without posting: the bytes reach the page another way (a scrollback replay
	/// that already contains them), so posting them too would double-paint.</summary>
	public void Discard() {
		lock (_gate) {
			_buffer.SetLength(0);
			DisarmLocked();
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			_disposed = true;
			_buffer.SetLength(0);
			DisarmLocked();
			_timer?.Dispose();
		}
	}

	// Posts the buffered bytes as one frame and disarms the pending flush. Caller holds _gate.
	private void FlushLocked() {
		if (_buffer.Length == 0) {
			DisarmLocked();
			return;
		}

		byte[] bytes = _buffer.ToArray();
		_buffer.SetLength(0);
		DisarmLocked();
		_post(bytes);
	}

	private void DisarmLocked() {
		if (_armed) {
			_armed = false;
			_timer?.Change(Timeout.Infinite, Timeout.Infinite);
		}
	}
}
