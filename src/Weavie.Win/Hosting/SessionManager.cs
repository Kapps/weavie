using Weavie.Core.Worktrees;

namespace Weavie.Win.Hosting;

/// <summary>
/// Owns the set of <see cref="HostSession"/>s in one workspace window and which one is <see cref="Active"/>
/// (what the page is bound to), plus the workspace's <see cref="WorktreeManager"/>. v1 starts with a single
/// primary session — identical to before, since <see cref="Active"/> is then always that one session;
/// additional sessions are created on git worktrees and the window re-routes the page to the active one on
/// switch. A pure holder: the window owns construction, per-session wiring, and switch orchestration.
/// </summary>
internal sealed class SessionManager : IAsyncDisposable {
	private readonly List<HostSession> _sessions = [];
	private readonly Lock _gate = new();

	/// <summary>Creates the manager over <paramref name="worktrees"/> (the workspace's worktree manager, or <c>null</c> when the root is not a git repo).</summary>
	public SessionManager(WorktreeManager? worktrees) {
		Worktrees = worktrees;
	}

	/// <summary>The workspace's worktree manager, or <c>null</c> when the workspace root is not a git repo.</summary>
	public WorktreeManager? Worktrees { get; }

	/// <summary>The currently bound session (what the page shows), or <c>null</c> before the first is added.</summary>
	public HostSession? Active { get; private set; }

	/// <summary>Snapshot of all sessions, in creation order. Safe to enumerate.</summary>
	public IReadOnlyList<HostSession> Sessions {
		get {
			lock (_gate) {
				return [.. _sessions];
			}
		}
	}

	/// <summary>Adds <paramref name="session"/>; makes it active when <paramref name="activate"/> is set (or it's the first).</summary>
	public void Add(HostSession session, bool activate) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_gate) {
			_sessions.Add(session);
			if (activate || Active is null) {
				Active = session;
			}
		}
	}

	/// <summary>Marks <paramref name="session"/> as the active (bound) session.</summary>
	public void SetActive(HostSession session) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_gate) {
			Active = session;
		}
	}

	/// <summary>Finds a session by its id, or <c>null</c>.</summary>
	public HostSession? Find(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			return _sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
		}
	}

	/// <summary>Removes <paramref name="session"/>; if it was active, the most-recent remaining session becomes active.</summary>
	public void Remove(HostSession session) {
		ArgumentNullException.ThrowIfNull(session);
		lock (_gate) {
			_sessions.Remove(session);
			if (ReferenceEquals(Active, session)) {
				Active = _sessions.Count > 0 ? _sessions[^1] : null;
			}
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		HostSession[] snapshot;
		lock (_gate) {
			snapshot = [.. _sessions];
			_sessions.Clear();
			Active = null;
		}

		foreach (var session in snapshot) {
			await session.DisposeAsync().ConfigureAwait(false);
		}
	}
}
