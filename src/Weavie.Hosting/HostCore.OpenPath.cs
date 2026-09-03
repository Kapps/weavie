using Weavie.Hosting.Desktop;

namespace Weavie.Hosting;

// Opening a path the OS delivered. The page decides which session shows it — the one the user is looking at,
// which may belong to another backend entirely — so the host only forwards, and only once a page is listening.
public sealed partial class HostCore {
	private readonly PendingOpens _pendingOpens = new();

	/// <summary>Asks the page to reveal <paramref name="path"/> in whichever session is selected.</summary>
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
		foreach (string path in _pendingOpens.Drain()) {
			_messages.Host.Feature("files").Publish("openPath", new { path });
		}
	}
}
