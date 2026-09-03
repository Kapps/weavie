using Weavie.Hosting.Desktop;

namespace Weavie.Hosting;

// Opening a path the OS delivered. The page decides which session shows it — the one the user is looking at,
// which may belong to another backend entirely — so the host only forwards, and only once a page is listening.
public sealed partial class HostCore {
	private readonly PendingOpens _pendingOpens = new();

	/// <summary>Asks the page to reveal <paramref name="path"/> in a session that can serve it.</summary>
	public void RequestOpenPath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		_pendingOpens.Add(path);
		FlushPendingOpens();
	}

	// Called from the page's `ready` handler, once its bridge can actually receive a push.
	private void MarkOpenPathPageReady() {
		_pendingOpens.MarkPageReady();
		FlushPendingOpens();
	}

	private void FlushPendingOpens() {
		var pending = _pendingOpens.Drain();
		if (pending.Count == 0) {
			return;
		}

		// The page prefers the selected session, but that one can belong to a backend this file never reaches;
		// this names the checkout to use instead, since only the host knows which slot that is.
		string? fallbackSlot = _sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout)?.Id;
		foreach (string path in pending) {
			_messages.Host.Feature("files").Publish("openPath", new { path, fallbackSlot });
		}
	}
}
