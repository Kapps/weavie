using System.Collections.Concurrent;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly ConcurrentDictionary<SpellRequestKey, CancellationTokenSource> _spellRequests = new();

	private readonly record struct SpellRequestKey(string SessionId, string Kind, string RequestId);

	private CancellationTokenSource BeginSpellRequest(SpellRequestKey key) {
		var cancellation = new CancellationTokenSource();
		if (_spellRequests.TryGetValue(key, out var previous)) {
			Cancel(previous);
		}

		_spellRequests[key] = cancellation;
		return cancellation;
	}

	private void CompleteSpellRequest(SpellRequestKey key, CancellationTokenSource cancellation, Action action) {
		_ui.Post(() => {
			try {
				if (IsCurrentSpellRequest(key, cancellation) && !cancellation.IsCancellationRequested) {
					action();
				}
			} finally {
				EndSpellRequest(key, cancellation);
			}
		});
	}

	private bool IsCurrentSpellRequest(SpellRequestKey key, CancellationTokenSource cancellation) =>
		_spellRequests.TryGetValue(key, out var current) && ReferenceEquals(current, cancellation);

	private void EndSpellRequest(SpellRequestKey key, CancellationTokenSource cancellation) {
		if (IsCurrentSpellRequest(key, cancellation)) {
			_spellRequests.TryRemove(key, out _);
		}

		cancellation.Dispose();
	}

	private void CancelAllSpellRequests() {
		foreach (var cancellation in _spellRequests.Values) {
			Cancel(cancellation);
		}
	}

	private void CancelSpellRequestsForSession(HostSession session) {
		foreach (var (key, cancellation) in _spellRequests) {
			if (string.Equals(key.SessionId, session.Id, StringComparison.Ordinal)) {
				Cancel(cancellation);
			}
		}
	}

	private static void Cancel(CancellationTokenSource cancellation) {
		try {
			cancellation.Cancel();
		} catch (ObjectDisposedException) {
			// A completion raced this cancellation; it already released the request.
		}
	}
}
