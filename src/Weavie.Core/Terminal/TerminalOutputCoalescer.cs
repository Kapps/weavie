using Weavie.Core.Coalescing;

namespace Weavie.Core.Terminal;

/// <summary>
/// Batches a terminal pane's live PTY output into fewer, larger frames before it crosses the bridge to the page.
/// One frame per PTY read chunk overwhelms the headless bridge's bounded outbox on a burst — a page can't drain
/// ~15k tiny frames as fast as a flood produces them, so the connection is dropped and the UI freezes. Output
/// accumulates here and is emitted as one frame when it reaches a byte threshold or the time window elapses,
/// whichever comes first, cutting a 38&#160;MB burst from ~15000 frames to ~150. Only the live post is deferred —
/// scrollback logging stays immediate. A window of 0 posts each chunk inline (no batching). See
/// <see cref="CoalescerBase"/> for the shared window machinery.
/// </summary>
public sealed class TerminalOutputCoalescer : CoalescerBase {
	// A burst flushes on this many buffered bytes, keeping frames-per-burst far under the outbox's 512-message
	// cap (a 38MB burst → ~150 frames) without inflating any single frame's base64/WebSocket cost.
	private const int FlushThresholdBytes = 256 * 1024;

	private readonly Action<byte[]> _post;
	private readonly MemoryStream _buffer = new();

	/// <summary>
	/// Coalesces output posted through <paramref name="post"/> (the bridge sink), flushing after
	/// <paramref name="windowMs"/> milliseconds or the byte threshold. <paramref name="windowMs"/> of 0 posts
	/// each chunk inline.
	/// </summary>
	public TerminalOutputCoalescer(Action<byte[]> post, long windowMs) : base(windowMs) {
		ArgumentNullException.ThrowIfNull(post);
		_post = post;
	}

	/// <summary>Buffers one live output chunk, flushing inline when the window is disabled or the buffer is now
	/// full; otherwise arms the time-window flush. Runs on the PTY read thread; never blocks it.</summary>
	public void Add(ReadOnlySpan<byte> data) {
		if (data.IsEmpty) {
			return;
		}

		lock (Gate) {
			if (IsDisposed) {
				return;
			}

			if (!WindowEnabled) {
				_post(data.ToArray());
				return;
			}

			_buffer.Write(data);
			if (_buffer.Length >= FlushThresholdBytes) {
				FlushLocked();
			} else {
				ArmLocked();
			}
		}
	}

	/// <inheritdoc/>
	protected override bool HasBuffered => _buffer.Length > 0;

	/// <inheritdoc/>
	protected override void ClearBuffer() => _buffer.SetLength(0);

	/// <inheritdoc/>
	protected override void FlushBufferLocked() {
		byte[] bytes = _buffer.ToArray();
		_buffer.SetLength(0);
		_post(bytes);
	}
}
