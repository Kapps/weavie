using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Changes;
using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// The loaded overlay on top of git-worktree reconciliation. Client selection is deliberately absent.
public sealed partial class HostCore {
	private const int MaxConcurrentSessionRestores = 8;

	private readonly List<ToastMessage> _sessionStartupNotices = [];

	private void PersistSessionState() {
		if (_sessions is null) {
			return;
		}

		_sessionStore.Save([.. _sessions.Slots.Select(slot => new SessionDescriptor {
			Id = new SessionId(slot.Id),
			Label = slot.Label,
			LastActiveAt = slot.LastActiveAt,
			Unseen = slot.Unseen,
			WorktreePath = slot.WorktreePath,
			WorkflowDocumentsDirectory = slot.WorkflowDocumentsDirectory,
			Device = slot.Device.Current.Home,
			// A session git couldn't place comes back as it was saved once its repository does.
			Loaded = slot.Lost?.Loaded ?? slot.Loaded,
			AgentProviderId = slot.AgentProviderId,
			EditorSession = slot.EditorSession,
			ShellTerminals = slot.ShellTerminals,
		})]);
	}

	private async Task RestoreSessionStateAsync() {
		if (_sessions is null) {
			return;
		}

		var saved = _sessionStore.Items;
		var foreign = await PlaceForeignSessionsAsync([.. saved.Where(item =>
			_sessions.Find(SessionSlot.IdFor(item.WorktreePath)) is null
			&& !IsWorkspaceCheckout(item.WorktreePath)
			&& Directory.Exists(item.WorktreePath))]).ConfigureAwait(false);
		var toLoad = new List<SessionSlot>();
		foreach (var item in saved) {
			foreign.TryGetValue(item.WorktreePath, out var placed);
			var slot = _sessions.Find(SessionSlot.IdFor(item.WorktreePath));
			if (slot is null && IsWorkspaceCheckout(item.WorktreePath)) {
				_sessions.Add(slot = DormantSlot(item with { WorktreePath = HomeFolder }, Home, _workspaceSessionLabel, null));
			} else if (slot is { }) {
				slot.EditorSession = item.EditorSession;
				slot.ShellTerminals = item.ShellTerminals;
			} else if (placed.Repo is { } repo && repo != Home) {
				_sessions.Add(slot = DormantSlot(item, repo, placed.Label, placed.Lost is null ? null : new(placed.Lost, item.Loaded)));
				if (placed.Lost is null) {
					Join(repo);
				} else {
					_sessionStartupNotices.Add(ToastMessage.Plain("warn",
						$"The session '{item.Label}' can't find its repository: {placed.Lost} It can't be loaded until the "
						+ "repository is back; delete the session to drop it."));
				}
			}

			if (slot is { }) {
				slot.WorkflowDocumentsDirectory = item.WorkflowDocumentsDirectory;
				slot.Device.Restore(item.Device);
				slot.LastActiveAt = item.LastActiveAt;
				slot.Unseen = item.Unseen;
			}

			if (item.Loaded && slot is { Lost: null }) {
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

		// A workspace opens with a session on its home folder until the user deletes that session. One with no
		// available agent provider still opens, with its other sessions and the reason why.
		if (_sessionStore.HomeClosed || _sessions.Slots.Any(IsWorkspaceCheckout)) {
			return;
		}

		try {
			OpenHomeSession(AvailableWorkspaceSessionProvider());
		} catch (Exception error) {
			_sessionStartupNotices.Add(
				ToastMessage.Plain("error", $"Couldn't open a session on this workspace's home folder: {Innermost(error).Message}"));
			Log($"[sessions] opening the home folder's session failed: {error}");
		}
	}

	// Where git places each saved session outside the home repo: its repo, opened once however many of its
	// sessions are saved, and its branch now. One git can't place stays listed on its own folder, with git's reason.
	private async Task<IReadOnlyDictionary<string, (Repo Repo, string Label, string? Lost)>> PlaceForeignSessionsAsync(
		IReadOnlyList<SessionDescriptor> items) {
		var git = new GitService();
		var repos = new ConcurrentDictionary<string, Lazy<Task<Repo>>>(PathIdentity.Comparer);
		var placed = new ConcurrentDictionary<string, (Repo, string, string?)>(PathIdentity.Comparer);
		if (items.Count == 0) {
			return placed;
		}

		try {
			// Asked once here, not once by each of the sessions placed side by side.
			await HomeMainWorktreeAsync(git, CancellationToken.None).ConfigureAwait(false);
		} catch (GitException) {
			// Each session's own placement reports it.
		}

		await Parallel.ForEachAsync(
			items,
			new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentSessionRestores },
			async (item, ct) => {
				Repo repo;
				try {
					string root = await RepoRootAsync(item.WorktreePath, ct).ConfigureAwait(false);
					repo = await repos.GetOrAdd(root, _ => new(() => RepoAtAsync(root, CancellationToken.None))).Value.ConfigureAwait(false);
				} catch (GitException ex) {
					placed[item.WorktreePath] = (OpenRepo(item.WorktreePath, git, isRepository: false), item.Label, ex.Message);
					return;
				}

				placed[item.WorktreePath] = (repo, await BranchLabelAsync(git, item, ct).ConfigureAwait(false), null);
			}).ConfigureAwait(false);
		return placed;
	}

	// The branch git has checked out there now, or the saved label where it names none (detached, or not yet born).
	private static async Task<string> BranchLabelAsync(GitService git, SessionDescriptor item, CancellationToken ct) {
		try {
			return await git.GetCurrentBranchAsync(item.WorktreePath, ct).ConfigureAwait(false) ?? item.Label;
		} catch (GitException) {
			return item.Label;
		}
	}

	private SessionSlot DormantSlot(SessionDescriptor item, Repo repo, string label, LostRepository? lost) => new() {
		Label = label,
		Device = NewDevice(null),
		Repo = repo,
		WorktreePath = item.WorktreePath,
		AgentProviderId = item.AgentProviderId,
		Session = null,
		EditorSession = item.EditorSession,
		ShellTerminals = item.ShellTerminals,
		Lost = lost,
	};

	private void RestoreSlot(SessionSlot slot, Lock publicationGate) {
		HostSession? session = null;
		try {
			session = CreateSession(slot);
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
				var retry = failure is InvalidReviewCheckpointException ? new ToastAction("Load session", SessionCommands.LoadSession,
					JsonSerializer.Serialize(new SessionSlotArgs(slot.Id), WireJson.Default.SessionSlotArgs)) : null;
				_sessionStartupNotices.Add(new("error", ReportSessionRestoreFailure(slot, failure), $"session-restore:{slot.Id}", retry));
			}
		}
	}

	private string ReportSessionRestoreFailure(SessionSlot slot, Exception error) {
		Log($"[sessions] restoring '{slot.Label}' failed: {error}");
		return $"Couldn't restore the session '{NameOf(slot)}': {Innermost(error).Message}";
	}

	// Raised while no page is connected, so the notices wait for the first hello (as the crash report does).
	private void SurfaceSessionStartupNotices() {
		foreach (var notice in _sessionStartupNotices) {
			Notify(notice);
		}

		_sessionStartupNotices.Clear();
	}

	private static Exception Innermost(Exception error) =>
		error is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : error;
}

internal sealed record CorruptReviewRefusal(string CorruptReviewSlot, string SessionName);
