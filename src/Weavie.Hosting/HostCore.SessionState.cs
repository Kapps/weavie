using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The loaded overlay on top of git-worktree reconciliation. Client selection is deliberately absent.
public sealed partial class HostCore {
	private void PersistSessionState() {
		if (_sessions is null) {
			return;
		}

		var sessions = _sessions.Slots
			.Select(slot => new SessionDescriptor {
				Id = new SessionId(slot.Id),
				Label = slot.Label,
				WorktreePath = slot.WorktreePath,
				ManagedCheckout = slot.ManagedCheckout,
				Loaded = slot.Loaded,
				AgentProviderId = slot.AgentProviderId,
				EditorSession = slot.EditorSession,
			})
			.ToList();
		_sessionStore.Save(sessions);
	}

	private void RestoreSessionState() {
		if (_sessions is null) {
			return;
		}

		bool firstOpen = _sessionStore.Items.Count == 0;
		var toLoad = new List<SessionSlot>();
		foreach (var item in _sessionStore.Items) {
			var slot = _sessions.Find(item.Id.Value)
				?? _sessions.Slots.FirstOrDefault(candidate => PathsEqual(candidate.WorktreePath, item.WorktreePath));
			if (slot is null && !item.ManagedCheckout && PathsEqual(item.WorktreePath, WorkspaceRoot)) {
				slot = new SessionSlot {
					Id = item.Id.Value,
					Label = _workspaceSessionLabel,
					WorktreePath = WorkspaceRoot,
					ManagedCheckout = false,
					AgentProviderId = item.AgentProviderId,
					Session = null,
					EditorSession = item.EditorSession,
				};
				_sessions.Add(slot);
			} else if (slot is { }) {
				slot.EditorSession = item.EditorSession;
			}

			if (item.Loaded && slot is { }) {
				toLoad.Add(slot);
			}
		}

		foreach (var slot in toLoad) {
			LoadSlotInBackground(slot);
		}

		if (firstOpen || _sessions.Slots.Count == 0) {
			EnsureWorkspaceSession();
		}
	}
}
