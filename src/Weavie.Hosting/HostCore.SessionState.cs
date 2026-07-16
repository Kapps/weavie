using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The loaded/active overlay on top of git-worktree reconciliation: persist which non-primary slots are loaded
// and which is active, and replay it at open so an auto-update restart returns as-left, not primary-only. See
// docs/specs/runner-auto-update.md.
public sealed partial class HostCore {
	// Writes the current non-primary slots (id, label, path, loaded) and the active non-primary slot to the store.
	// Called wherever the loaded set or active slot changes (switch / background-load / unload / delete).
	private void PersistSessionState() {
		if (_sessions is null) {
			return;
		}

		var sessions = _sessions.Slots
			.Where(slot => !slot.IsPrimary)
			.Select(slot => new SessionDescriptor {
				Id = new SessionId(slot.Id),
				Label = slot.Label,
				WorktreePath = slot.WorktreePath,
				IsPrimary = false,
				Loaded = slot.Loaded,
				AgentProviderId = slot.AgentProviderId,
			})
			.ToList();
		// A null active means the primary is active — it's always loaded, so it needs no overlay entry.
		var active = _sessions.ActiveSlot is { IsPrimary: false } activeSlot ? new SessionId(activeSlot.Id) : (SessionId?)null;
		_sessionStore.Save(sessions, active);
	}

	// Capture the overlay before loading: each load below re-persists (the primary is still active until the
	// switch), which would clobber activeId mid-restore. Slots the reconcile didn't surface (worktree removed
	// out-of-band) are skipped — the live git set always wins over the stored overlay.
	private void RestoreSessionState() {
		if (_sessions is null) {
			return;
		}

		var persisted = _sessionStore.Items;
		var activeId = _sessionStore.ActiveId;

		foreach (var item in persisted) {
			if (item.Loaded && _sessions.Find(item.Id.Value) is { IsPrimary: false } slot) {
				LoadSlotInBackground(slot);
			}
		}

		if (activeId is { } id && _sessions.Find(id.Value) is { IsPrimary: false } activeSlot) {
			SwitchToSlot(activeSlot, replayAgentState: true);
		}
	}
}
