namespace Weavie.Hosting;

// A session is rooted at a working directory. When that directory is deleted out from under it — a worktree
// removed in a terminal, a folder moved away — nothing rooted there can work, so the session ends here instead
// of leaving a dead slot on the rail that reports git's missing-directory words on every reconnect.
public sealed partial class HostCore {
	private async Task CloseVanishedSessionAsync(HostSession session) {
		bool closed = false;
		try {
			closed = await RunSessionLifecycleAsync(
				() => CloseVanishedSessionCoreAsync(session),
				CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) {
			Log($"[sessions] closing the vanished session '{session.DisplayLabel}' failed: {ex}");
			Notify("error", $"Couldn't close session '{session.DisplayLabel}', whose folder no longer exists: {ex.Message}");
		} finally {
			// A close that didn't happen leaves a live session, so the next observer to find the directory
			// missing has to be able to raise it again.
			if (!closed) {
				session.ResumeWorkspaceRootWatch();
			}
		}
	}

	private async Task<bool> CloseVanishedSessionCoreAsync(HostSession session) {
		// Read the slot under the lifecycle gate: a delete of this same session may already have taken it, and
		// the directory may have come back inside the window.
		if (SlotFor(session) is not { } slot || Directory.Exists(slot.WorktreePath)) {
			return false;
		}

		Log($"[sessions] '{slot.Label}' lost its working directory {slot.WorktreePath}: {session.WorkspaceRootGoneCause}");

		// The workspace's own checkout is a catalog invariant Weavie re-creates, so closing it would only loop.
		// It is the whole workspace that is gone, and the user is the only one who can act on that.
		if (IsWorkspaceCheckout(slot)) {
			session.ReportWorkspaceRootGone(
				$"This workspace's folder no longer exists: {slot.WorktreePath}. Open a workspace that does.");
			return true;
		}

		await _ui.InvokeAsync(() => UnloadSlotAsync(slot), CancellationToken.None).ConfigureAwait(false);
		await RetireSlotAsync(slot).ConfigureAwait(false);
		if (_worktrees is { } worktrees) {
			await worktrees.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
		}

		// Error level so it stands until dismissed: this notice is the only account of a destroyed session, and a
		// user away from the screen would otherwise find it simply gone from the rail.
		Notify("error", $"Session '{slot.Label}' was closed: its worktree {slot.WorktreePath} no longer exists.");
		return true;
	}
}
