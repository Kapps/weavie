namespace Weavie.Core.Workspaces;

/// <summary>The outcome of an open request: identity + normalized root, and whether a window was already
/// open (so the host focuses the existing one instead of opening another).</summary>
public readonly record struct WorkspaceOpen(WorkspaceId Id, string Root, bool AlreadyOpen);

/// <summary>
/// App-level workspace orchestration shared by every host: tracks open workspaces, dedupes an open request
/// against an existing window (by <see cref="WorkspaceId"/>), and keeps <see cref="RecentWorkspaces"/> current.
/// </summary>
public sealed class WorkspaceManager {
	private readonly HashSet<WorkspaceId> _open = [];
	private readonly Lock _gate = new();

	/// <summary>Creates the manager over the app-global <paramref name="recents"/> store.</summary>
	public WorkspaceManager(RecentWorkspaces recents) {
		ArgumentNullException.ThrowIfNull(recents);
		Recents = recents;
	}

	/// <summary>The recent-workspaces store, for the Open Recent menu and launch restore.</summary>
	public RecentWorkspaces Recents { get; }

	/// <summary>The number of workspaces currently open.</summary>
	public int OpenCount {
		get { lock (_gate) { return _open.Count; } }
	}

	/// <summary>Whether a workspace with <paramref name="id"/> is currently open.</summary>
	public bool IsOpen(WorkspaceId id) {
		lock (_gate) { return _open.Contains(id); }
	}

	/// <summary>
	/// Records a request to open <paramref name="root"/>: normalizes it, bumps recents, marks it open.
	/// <see cref="WorkspaceOpen.AlreadyOpen"/> is <c>true</c> when a window exists — the host should focus it.
	/// </summary>
	public WorkspaceOpen Open(string root) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		string full = Path.GetFullPath(root);
		var id = WorkspaceId.ForPath(full);
		bool alreadyOpen;
		lock (_gate) {
			alreadyOpen = !_open.Add(id);
		}

		Recents.Add(full);
		return new WorkspaceOpen(id, full, alreadyOpen);
	}

	/// <summary>Marks the workspace with <paramref name="id"/> closed (its window was closed).</summary>
	public void Close(WorkspaceId id) {
		lock (_gate) {
			_open.Remove(id);
		}
	}
}
