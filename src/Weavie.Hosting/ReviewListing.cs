namespace Weavie.Hosting;

/// <summary>
/// The files the watcher saw change that an open git review doesn't show yet, whether the review must be listed from
/// git in full, and the HEAD moves a listing must not straddle.
/// </summary>
internal sealed class ReviewListing {
	private readonly Lock _gate = new();
	// Each pending path with the generation it was last seen changing in.
	private readonly Dictionary<string, long> _pending = new(Core.FileSystem.PathIdentity.Comparer);
	private long _generation;
	private long _fullAt = -1;
	private long _headMoves;
	private long _listings;

	/// <summary>Records files the review doesn't show as they are on disk, to join it one by one.</summary>
	public void Changed(IEnumerable<string> paths) {
		lock (_gate) {
			_generation++;
			foreach (string path in paths) _pending[path] = _generation;
		}
	}

	/// <summary>Records that anything may have changed unseen, so the review is listed from git in full.</summary>
	public void RequireFull() {
		lock (_gate) _fullAt = ++_generation;
	}

	/// <summary>Records that HEAD or the branch it names may have moved.</summary>
	public void HeadMoved() {
		lock (_gate) _headMoves++;
	}

	/// <summary>Observes the state now, as the start of a listing or of a look at whether one is needed.</summary>
	public ListingState Observe() {
		lock (_gate) return new(_generation, _headMoves, _listings, _pending.Count > 0, _fullAt >= 0);
	}

	/// <summary>Whether HEAD may have moved since <paramref name="start"/>, so a listing read a mix of two commits.</summary>
	public bool HeadMovedSince(ListingState start) {
		lock (_gate) return _headMoves != start.HeadMoves;
	}

	/// <summary>
	/// Finishes a full listing. One that <paramref name="covers"/> its start (it read git, or failed and shouldn't be
	/// retried in a loop) settles everything seen before it began; an abandoned one leaves it for the next.
	/// </summary>
	public void Finish(ListingState start, bool covers) {
		lock (_gate) {
			if (covers) {
				foreach (var (path, seen) in _pending.ToArray())
					if (seen <= start.Generation) _pending.Remove(path);
				if (_fullAt <= start.Generation) _fullAt = -1;
			}
			_listings++;
		}
	}

	/// <summary>Takes the pending files to join the review at its current base.</summary>
	public IReadOnlyList<string> TakePending() {
		lock (_gate) {
			string[] paths = [.. _pending.Keys];
			_pending.Clear();
			return paths;
		}
	}

	/// <summary>Whether the review has changes to pick up, one by one or in full.</summary>
	public bool Stale {
		get {
			lock (_gate) return _pending.Count > 0 || _fullAt >= 0;
		}
	}
}

/// <summary>
/// The review's listing state at one moment: its generation, the HEAD moves and finished listings seen, and whether
/// files are pending or a full listing is due.
/// </summary>
internal readonly record struct ListingState(long Generation, long HeadMoves, long Listings, bool Pending, bool Full) {
	/// <summary>Whether anything is due, so the review waits out a git operation still rewriting the worktree.</summary>
	public bool Stale => Pending || Full;
}
