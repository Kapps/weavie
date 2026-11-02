using Weavie.Core.Agents;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

/// <summary>
/// Holds one agent turn until the session's status is one of <c>deliverOn</c>, then sends it once; a send that throws
/// is reported to <c>failed</c>. A newer turn replaces a pending one; after <see cref="Dispose"/> nothing is sent.
/// </summary>
internal sealed class IdleTurnDelivery(
	SessionStatusMachine status,
	IReadOnlySet<SessionStatus> deliverOn,
	Action<AgentTurnSubmission> send,
	Action<AgentTurnSubmission, Exception> failed) : IDisposable {
	private readonly Lock _gate = new();
	private AgentTurnSubmission? _pending;
	private bool _closed;

	/// <summary>Holds <paramref name="turn"/> until it can be sent.</summary>
	public void Queue(AgentTurnSubmission turn) {
		lock (_gate) {
			ObjectDisposedException.ThrowIf(_closed, this);
			if (_pending is null) status.Changed += TryDeliver;
			_pending = turn;
		}

		// Sent off the caller's thread: callers queue while holding their own locks.
		_ = Task.Run(() => TryDeliver(status.Status));
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			_closed = true;
			Clear();
		}
	}

	private void TryDeliver(SessionStatus current) {
		if (!deliverOn.Contains(current)) {
			return;
		}

		AgentTurnSubmission pending;
		lock (_gate) {
			if (_pending is not { } owed || !deliverOn.Contains(status.Status)) {
				return;
			}

			pending = owed;
			Clear();
		}

		try {
			send(pending);
		} catch (Exception ex) {
			failed(pending, ex);
		}
	}

	private void Clear() {
		if (_pending is not null) status.Changed -= TryDeliver;
		_pending = null;
	}
}
