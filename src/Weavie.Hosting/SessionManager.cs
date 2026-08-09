using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

/// <summary>
/// Owns the rail's <see cref="SessionSlot"/>s for one workspace (the root checkout plus every surfaced worktree, each
/// loaded or dormant), plus the <see cref="WorktreeManager"/>. Selection belongs to each client, never the host.
/// </summary>
public sealed class SessionManager : IAsyncDisposable {
	private readonly List<SessionSlot> _slots = [];
	private readonly Lock _gate = new();

	/// <summary>Creates the manager over <paramref name="worktrees"/> (the workspace's worktree manager, or <c>null</c> when the root is not a git repo).</summary>
	public SessionManager(WorktreeManager? worktrees) {
		Worktrees = worktrees;
	}

	/// <summary>The workspace's worktree manager, or <c>null</c> when the workspace root is not a git repo.</summary>
	public WorktreeManager? Worktrees { get; }

	/// <summary>Snapshot of all slots, in creation order. Safe to enumerate.</summary>
	public IReadOnlyList<SessionSlot> Slots {
		get {
			lock (_gate) {
				return [.. _slots];
			}
		}
	}

	/// <summary>Adds <paramref name="slot"/>.</summary>
	public void Add(SessionSlot slot) {
		ArgumentNullException.ThrowIfNull(slot);

		lock (_gate) {
			_slots.Add(slot);
		}
	}

	/// <summary>Finds a slot by its id, or <c>null</c>.</summary>
	public SessionSlot? Find(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			return _slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
		}
	}

	/// <summary>Removes <paramref name="slot"/> entirely (for example after its worktree is deleted).</summary>
	public void Remove(SessionSlot slot) {
		ArgumentNullException.ThrowIfNull(slot);
		lock (_gate) {
			_slots.Remove(slot);
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		SessionSlot[] snapshot;
		lock (_gate) {
			snapshot = [.. _slots];
			_slots.Clear();
		}

		var failures = new List<Exception>();
		foreach (var slot in snapshot) {
			if (slot.Session is { } session) {
				try {
					await session.DisposeAsync().ConfigureAwait(false);
				} catch (Exception ex) {
					failures.Add(ex);
				}
			}
		}

		if (failures.Count > 0) {
			throw new AggregateException("One or more sessions failed to shut down.", failures);
		}
	}
}
