using Weavie.Core.Commands;
using Weavie.Hosting.Desktop;
using Weavie.Hosting.Messaging;

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

		// No session here can show a file of this machine, and holding it would open it in whichever comes next.
		if (LoadedSessions().Count == 0) {
			foreach (string path in pending) {
				Notify(
					"warn", $"Couldn't open {path}: this window has no loaded session to show it in.", $"open-path:{path}",
					"Open Home Folder Session", SessionCommands.OpenHomeSession, null);
			}

			return;
		}

		// The page prefers the selected session, but that one can belong to a backend this file never reaches;
		// this names the checkout to use instead, since only the host knows which slot that is.
		string? fallbackSlot = _sessions?.Slots.FirstOrDefault(IsWorkspaceCheckout)?.Id;
		foreach (string path in pending) {
			_messages.Host.Feature("files").Publish("openPath", WireJson.Default.OpenPathRequest, new(path, fallbackSlot));
		}
	}
}

internal sealed record OpenPathRequest(string Path, string? FallbackSlot);
