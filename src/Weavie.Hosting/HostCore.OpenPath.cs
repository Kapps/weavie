using Weavie.Hosting.Desktop;

namespace Weavie.Hosting;

// Opening a path the OS delivered. It can arrive before there is a page or a loaded session, so it waits for
// both rather than being published into a broadcast that drops it.
public sealed partial class HostCore {
	private readonly PendingOpens _pendingOpens = new();

	/// <summary>Reveals <paramref name="path"/> in this workspace once a page and a session can receive it.</summary>
	public void RequestOpenPath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		_pendingOpens.Add(path);
		FlushPendingOpens();
	}

	// Called from the page's `ready` handler and whenever a session loads — either can be the missing half.
	private void MarkOpenPathPageReady() {
		_pendingOpens.MarkPageReady();
		FlushPendingOpens();
	}

	private void FlushPendingOpens() {
		var session = _sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout)?.Session;
		foreach (string path in _pendingOpens.Drain(session is not null)) {
			session!.FileOpener.Open(path, line: 1, preview: false, scratch: false);
		}
	}
}
