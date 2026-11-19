namespace Weavie.Hosting;

/// <summary>
/// Counts the watcher batches an open git review couldn't account for against the listings that read git after them,
/// and the HEAD moves a listing must not straddle.
/// </summary>
internal sealed class ReviewListing {
	private readonly Lock _gate = new();
	private long _invalidations;
	private long _covered;
	private long _headMoves;
	private long _listings;

	/// <summary>Records a watcher batch the review couldn't account for.</summary>
	public void Invalidate() {
		lock (_gate) _invalidations++;
	}

	/// <summary>Records that HEAD or the branch it names may have moved.</summary>
	public void HeadMoved() {
		lock (_gate) _headMoves++;
	}

	/// <summary>Observes the counts now, as the start of a listing or of a look at whether one is needed.</summary>
	public ListingState Observe() {
		lock (_gate) return new(_invalidations, _headMoves, _listings, _invalidations > _covered);
	}

	/// <summary>Whether HEAD may have moved since <paramref name="start"/>, so a listing read a mix of two commits.</summary>
	public bool HeadMovedSince(ListingState start) {
		lock (_gate) return _headMoves != start.HeadMoves;
	}

	/// <summary>
	/// Finishes a listing. One that <paramref name="covers"/> its start (it read git, or failed and shouldn't be retried
	/// in a loop) settles the invalidations before it; an abandoned one leaves them for the next.
	/// </summary>
	public void Finish(ListingState start, bool covers) {
		lock (_gate) {
			if (covers) _covered = Math.Max(_covered, start.Invalidations);
			_listings++;
		}
	}

	/// <summary>Whether some invalidation came after every covering listing started.</summary>
	public bool Stale {
		get {
			lock (_gate) return _invalidations > _covered;
		}
	}
}

/// <summary>
/// The review's listing counts at one moment: invalidations and HEAD moves seen, listings finished, and whether an
/// invalidation is still unlisted.
/// </summary>
internal readonly record struct ListingState(long Invalidations, long HeadMoves, long Listings, bool Stale);
