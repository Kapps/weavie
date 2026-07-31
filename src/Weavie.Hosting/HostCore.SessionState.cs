using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The loaded overlay on top of git-worktree reconciliation. Client selection is deliberately absent.
public sealed partial class HostCore {
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
		_sessionStore.Save(sessions);
	}

	private void RestoreSessionState() {
		if (_sessions is null) {
			return;
		}

		var persisted = _sessionStore.Items;

		foreach (var item in persisted) {
			if (item.Loaded && _sessions.Find(item.Id.Value) is { IsPrimary: false } slot) {
				LoadSlotInBackground(slot);
			}
		}
	}
}
