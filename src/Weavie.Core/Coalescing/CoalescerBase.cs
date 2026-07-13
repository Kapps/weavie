namespace Weavie.Core.Coalescing;

/// <summary>
/// The shared time-window batching machinery for the bridge coalescers: buffered output flushes as one frame
/// after a short window (or, when the window is 0, subclasses post each item inline), so a burst can't flood the
/// headless bridge's bounded outbox and get a network-slow page dropped. Subclasses own the payload buffer and how
/// it is posted; this base owns the timer, the armed/disposed state, and Flush/Discard/Dispose.
/// <para>Flush/Discard/Dispose and the payload flush all run under <see cref="Gate"/>, so output order is preserved
/// across the producer, timer, and flush-boundary threads. The post callback must be non-blocking (the bridge's
/// <c>PostToWeb</c> is), the same contract inline posting relied on.</para>
/// </summary>
public abstract class CoalescerBase : IDisposable {
	private readonly long _windowMs;
	private readonly Timer? _timer;
	private bool _armed;

	/// <summary>Guards the buffer and the arm/flush state; a subclass takes it in its <c>Add</c>.</summary>
	protected Lock Gate { get; } = new();

	/// <summary>True once disposed; a subclass's <c>Add</c> must no-op then. Read under <see cref="Gate"/>.</summary>
	protected bool IsDisposed { get; private set; }

	/// <summary>Creates a coalescer flushing after <paramref name="windowMs"/> ms; 0 disables batching.</summary>
	protected CoalescerBase(long windowMs) {
		_windowMs = windowMs;
		if (windowMs > 0) {
			_timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
		}
	}

	/// <summary>Whether batching is on; when false a subclass posts each item inline instead of buffering.</summary>
	protected bool WindowEnabled => _windowMs > 0;

	/// <summary>Emits any buffered output as one frame now — call before a frame that must stay ordered after it.
	/// No-op when empty.</summary>
	public void Flush() {
		lock (Gate) {
			if (!IsDisposed) {
				FlushLocked();
			}
		}
	}

	/// <summary>Drops buffered output without posting: it reaches the page another way (a replay that already
	/// holds it, or a reset that wiped it), so posting it too would double-paint.</summary>
	public void Discard() {
		lock (Gate) {
			ClearBuffer();
			DisarmLocked();
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (Gate) {
			IsDisposed = true;
			ClearBuffer();
			DisarmLocked();
			_timer?.Dispose();
		}
	}

	/// <summary>Arms the window timer if not already armed. Caller holds <see cref="Gate"/>.</summary>
	protected void ArmLocked() {
		if (!_armed) {
			_armed = true;
			_timer!.Change(_windowMs, Timeout.Infinite);
		}
	}

	/// <summary>Posts the buffer as one frame (when non-empty) and disarms the pending flush. Caller holds
	/// <see cref="Gate"/> — used by a subclass that flushes inline on crossing a threshold.</summary>
	protected void FlushLocked() {
		if (HasBuffered) {
			FlushBufferLocked();
		}

		DisarmLocked();
	}

	/// <summary>Whether the buffer holds anything to flush. Caller holds <see cref="Gate"/>.</summary>
	protected abstract bool HasBuffered { get; }

	/// <summary>Empties the buffer without posting. Caller holds <see cref="Gate"/>.</summary>
	protected abstract void ClearBuffer();

	/// <summary>Posts the buffered payload as one frame and empties the buffer. Caller holds <see cref="Gate"/>.</summary>
	protected abstract void FlushBufferLocked();

	private void DisarmLocked() {
		if (_armed) {
			_armed = false;
			_timer?.Change(Timeout.Infinite, Timeout.Infinite);
		}
	}
}
