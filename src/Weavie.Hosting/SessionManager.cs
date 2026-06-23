using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

/// <summary>
/// Owns the rail's <see cref="SessionSlot"/>s for one workspace (the primary plus every surfaced worktree, each
/// loaded or dormant), which one is <see cref="ActiveSlot"/>, and the <see cref="WorktreeManager"/>. A pure
/// holder — <c>HostCore</c> owns construction, wiring, load/unload, and switch orchestration.
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

	/// <summary>The currently bound slot (what the page shows), or <c>null</c> before the first is added.</summary>
	public SessionSlot? ActiveSlot { get; private set; }

	/// <summary>Snapshot of all slots, in creation order. Safe to enumerate.</summary>
	public IReadOnlyList<SessionSlot> Slots {
		get {
			lock (_gate) {
				return [.. _slots];
			}
		}
	}

	/// <summary>Adds <paramref name="slot"/>; makes it active when <paramref name="activate"/> is set (or it's the first).</summary>
	public void Add(SessionSlot slot, bool activate) {
		ArgumentNullException.ThrowIfNull(slot);

		lock (_gate) {
			_slots.Add(slot);
			if (activate || ActiveSlot is null) {
				ActiveSlot = slot;
			}
		}
	}

	/// <summary>Marks <paramref name="slot"/> as the active (bound) slot.</summary>
	public void SetActive(SessionSlot slot) {
		ArgumentNullException.ThrowIfNull(slot);
		lock (_gate) {
			ActiveSlot = slot;
		}
	}

	/// <summary>Finds a slot by its id, or <c>null</c>.</summary>
	public SessionSlot? Find(string id) {
		ArgumentException.ThrowIfNullOrEmpty(id);
		lock (_gate) {
			return _slots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
		}
	}

	/// <summary>Removes <paramref name="slot"/> entirely (e.g. its worktree was deleted); if it was active, the most-recent remaining slot becomes active.</summary>
	public void Remove(SessionSlot slot) {
		ArgumentNullException.ThrowIfNull(slot);
		lock (_gate) {
			_slots.Remove(slot);
			if (ReferenceEquals(ActiveSlot, slot)) {
				ActiveSlot = _slots.Count > 0 ? _slots[^1] : null;
			}
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync() {
		SessionSlot[] snapshot;
		lock (_gate) {
			snapshot = [.. _slots];
			_slots.Clear();
			ActiveSlot = null;
		}

		foreach (var slot in snapshot) {
			if (slot.Session is { } session) {
				await session.DisposeAsync().ConfigureAwait(false);
			}
		}
	}
}
