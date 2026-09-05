using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The loaded overlay on top of git-worktree reconciliation. Client selection is deliberately absent.
public sealed partial class HostCore {
	private readonly List<(string Level, string Message)> _sessionStartupNotices = [];

	private void PersistSessionState() {
		if (_sessions is null) {
			return;
		}

		var sessions = _sessions.Slots
			.Select(slot => new SessionDescriptor {
				Id = new SessionId(slot.Id),
				Label = slot.Label,
				WorktreePath = slot.WorktreePath,
				Loaded = slot.Loaded,
				AgentProviderId = slot.AgentProviderId,
				EditorSession = slot.EditorSession,
				ShellTerminals = slot.ShellTerminals,
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
				?? _sessions.Slots.FirstOrDefault(candidate => PathIdentity.Equals(candidate.WorktreePath, item.WorktreePath));
			if (slot is null && IsWorkspaceCheckout(item.WorktreePath)) {
				slot = new SessionSlot {
					Id = item.Id.Value,
					Label = _workspaceSessionLabel,
					WorktreePath = WorkspaceRoot,
					AgentProviderId = item.AgentProviderId,
					Session = null,
					EditorSession = item.EditorSession,
					ShellTerminals = item.ShellTerminals,
				};
				_sessions.Add(slot);
			} else if (slot is { }) {
				slot.EditorSession = item.EditorSession;
				slot.ShellTerminals = item.ShellTerminals;
			}

			if (item.Loaded && slot is { }) {
				toLoad.Add(slot);
			}
		}

		foreach (var slot in toLoad) {
			// One session that can no longer load — a provider the user has since removed, a worktree that moved —
			// leaves that slot dormant and tells the user at hello. It never takes the whole host down with it.
			try {
				LoadSlotInBackground(slot);
			} catch (Exception error) {
				_sessionStartupNotices.Add(
					("error", $"Couldn't restore the session '{slot.Label}': {Innermost(error).Message}"));
				Log($"[sessions] restoring '{slot.Label}' failed: {error}");
			}
		}

		if (firstOpen || _sessions.Slots.Count == 0) {
			EnsureWorkspaceSession();
		}
	}

	// Raised while no page is connected, so the notices wait for the first hello (as the crash report does).
	private void SurfaceSessionStartupNotices() {
		foreach (var (level, message) in _sessionStartupNotices) {
			Notify(level, message);
		}

		_sessionStartupNotices.Clear();
	}

	private static Exception Innermost(Exception error) =>
		error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
}
