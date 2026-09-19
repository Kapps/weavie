using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The loaded overlay on top of git-worktree reconciliation. Client selection is deliberately absent.
public sealed partial class HostCore {
	private const int MaxConcurrentSessionRestores = 8;

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

	private async Task RestoreSessionStateAsync() {
		if (_sessions is null) {
			return;
		}

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

		// Native UI loops may not be running yet; publish serially without dispatching to them.
		// Bounded so a workspace with many loaded sessions doesn't fork all their Claude/shell
		// processes in one simultaneous burst at startup; this is process-spawn concurrency, not
		// CPU work, so the cap is a fixed constant rather than Environment.ProcessorCount.
		var publicationGate = new Lock();
		await Parallel.ForEachAsync(
			toLoad.Distinct(),
			new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSessionRestores },
			(slot, _) => {
				RestoreSlot(slot, publicationGate);
				return ValueTask.CompletedTask;
			}).ConfigureAwait(false);

		// The workspace's own checkout always has a session; it is re-created whenever nothing covers it. A
		// workspace with no available agent provider still opens, with its other sessions and the reason why.
		try {
			EnsureWorkspaceSession();
		} catch (Exception error) {
			_sessionStartupNotices.Add(
				("error", $"Couldn't open a session on this workspace's own checkout: {Innermost(error).Message}"));
			Log($"[sessions] ensuring the workspace-checkout session failed: {error}");
		}
	}

	private void RestoreSlot(SessionSlot slot, Lock publicationGate) {
		HostSession? session = null;
		try {
			session = CreateSession(slot.WorktreePath, slot.AgentProviderId, slot.Id, slot.ShellTerminals);
			RestoreSlotEditor(session, slot);
			ReplaySession(session);

			lock (publicationGate) {
				slot.Session = session;
				PushSessionList();
				session.ActivateOwnedRuntimeAndMessages();
				PersistSessionState();
			}

			StartSessionTerminals(session);
		} catch (Exception error) {
			lock (publicationGate) {
				var failure = RollbackSessionLoad(slot, session, removeSlot: false, error);
				_sessionStartupNotices.Add(
					("error", $"Couldn't restore the session '{slot.Label}': {Innermost(failure).Message}"));
				Log($"[sessions] restoring '{slot.Label}' failed: {failure}");
			}
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
