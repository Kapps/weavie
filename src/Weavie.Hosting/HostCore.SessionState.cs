using Weavie.Core.FileSystem;
using Weavie.Core.Git;
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
				LastActiveAt = slot.LastActiveAt,
				Unseen = slot.Unseen,
				WorktreePath = slot.WorktreePath,
				WorkflowDocumentsDirectory = slot.WorkflowDocumentsDirectory,
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
			var slot = _sessions.Find(SessionSlot.IdFor(item.WorktreePath));
			if (slot is null && IsWorkspaceCheckout(item.WorktreePath)) {
				slot = new SessionSlot {
					Label = _workspaceSessionLabel,
					Repo = Home,
					WorktreePath = HomeFolder,
					AgentProviderId = item.AgentProviderId,
					Session = null,
					EditorSession = item.EditorSession,
					ShellTerminals = item.ShellTerminals,
				};
				_sessions.Add(slot);
			} else if (slot is { }) {
				slot.EditorSession = item.EditorSession;
				slot.ShellTerminals = item.ShellTerminals;
			} else if (await RestoreForeignSlotAsync(item).ConfigureAwait(false) is { } foreign) {
				_sessions.Add(slot = foreign);
			}

			if (slot is { }) {
				slot.WorkflowDocumentsDirectory = item.WorkflowDocumentsDirectory;
				slot.LastActiveAt = item.LastActiveAt;
				slot.Unseen = item.Unseen;
			}

			if (item.Loaded && slot is { }) {
				toLoad.Add(slot);
			}
		}

		RestoreWorkflowDocuments();

		// Native UI loops may not be running yet; publish serially without dispatching to them.
		// Bounded so a workspace with many loaded sessions doesn't fork all their Claude/shell
		// processes in one simultaneous burst at startup; this is process-spawn concurrency, not
		// CPU work, so the cap is a fixed constant rather than Environment.ProcessorCount.
		var publicationGate = new Lock();
		await Parallel.ForEachAsync(
			toLoad.Distinct(),
			new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSessionRestores },
			async (slot, _) => await GatedAsync(slot.Lifecycle, () => {
				if (!slot.Loaded && ReferenceEquals(_sessions.Find(slot.Id), slot)) {
					RestoreSlot(slot, publicationGate);
				}
				return Task.CompletedTask;
			}, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

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

	// A saved session outside the home repo: git says which repo its worktree belongs to.
	private async Task<SessionSlot?> RestoreForeignSlotAsync(SessionDescriptor item) {
		if (!Directory.Exists(item.WorktreePath)) {
			return null;
		}

		try {
			var repo = await RepoForAsync(item.WorktreePath, CancellationToken.None).ConfigureAwait(false);
			return repo == Home ? null : new SessionSlot {
				Label = item.Label,
				Repo = repo,
				WorktreePath = item.WorktreePath,
				AgentProviderId = item.AgentProviderId,
				Session = null,
				EditorSession = item.EditorSession,
				ShellTerminals = item.ShellTerminals,
			};
		} catch (GitException ex) {
			_sessionStartupNotices.Add(("warn", $"The session '{item.Label}' wasn't restored: {ex.Message}"));
			return null;
		}
	}

	private void RestoreSlot(SessionSlot slot, Lock publicationGate) {
		HostSession? session = null;
		try {
			session = CreateSession(slot.Repo, slot.WorktreePath, slot.AgentProviderId, slot.ShellTerminals);
			session.InheritedWorkflowDocuments = slot.WorkflowDocumentsDirectory;
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
