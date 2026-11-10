using Weavie.Core.Sessions;

namespace Weavie.Hosting;

/// <summary>
/// Holds one piece of agent work until the session's status is one of <c>deliverOn</c>, then sends it once, off the
/// thread that raised the status; a send that fails is reported to <c>failed</c>. A newer item replaces a pending
/// one; after <see cref="Dispose"/> nothing is sent.
/// </summary>
internal sealed class IdleTurnDelivery<T>(
	SessionStatusMachine status,
	IReadOnlySet<SessionStatus> deliverOn,
	Func<T, Task> send,
	Action<T, Exception> failed) : IDisposable where T : class {
	private readonly Lock _gate = new();
	private T? _pending;
	private bool _closed;

	/// <summary>Holds <paramref name="item"/> until it can be sent.</summary>
	public void Queue(T item) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closed, this);
			if (_pending is null) status.Changed += TryDeliver;
			_pending = item;
		}

		TryDeliver(status.Status);
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			_closed = true;
			Clear();
		}
	}

	// Status changes fire under the agent's locks and callers queue under their own, so the send never runs inline.
	private void TryDeliver(SessionStatus current) {
		if (!deliverOn.Contains(current)) {
			return;
		}

		_ = Task.Run(async () => {
			T pending;
			lock (_gate) {
				if (_pending is not { } owed || !deliverOn.Contains(status.Status)) {
					return;
				}

				pending = owed;
				Clear();
			}

			try {
				await send(pending).ConfigureAwait(false);
			} catch (Exception ex) {
				failed(pending, ex);
			}
		});
	}

	private void Clear() {
		if (_pending is not null) status.Changed -= TryDeliver;
		_pending = null;
	}
}
