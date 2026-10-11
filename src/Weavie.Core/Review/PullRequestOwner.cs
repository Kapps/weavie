namespace Weavie.Core.Review;

/// <summary>
/// Follows one session's PR and owns a snapshot of it: every load and write runs behind one gate and publishes inside
/// it, so the last published snapshot is always the newest. Subclasses supply the load.
/// </summary>
public abstract class PullRequestOwner<TSnapshot>
	where TSnapshot : class {
	private readonly Action<TSnapshot> _publish;
	private volatile PullRequestTarget? _target;
	private volatile bool _tracking;
	private volatile TSnapshot _latest;

	/// <summary>Starts at <paramref name="empty"/>, publishing every later snapshot through <paramref name="publish"/>.</summary>
	protected PullRequestOwner(TSnapshot empty, Action<TSnapshot> publish) {
		ArgumentNullException.ThrowIfNull(empty);
		ArgumentNullException.ThrowIfNull(publish);
		_latest = empty;
		_publish = publish;
	}

	/// <summary>Serializes loads and writes.</summary>
	protected SemaphoreSlim Gate { get; } = new(1, 1);

	/// <summary>The PR being followed, or null when the branch has none.</summary>
	protected PullRequestTarget? Target => _target;

	/// <summary>The most recently published snapshot.</summary>
	public TSnapshot Latest => _latest;

	/// <summary>
	/// Follows the branch's PR (null: none), reloading only when the PR or its head/base changes, never merely because
	/// the branch was re-checked.
	/// </summary>
	public Task TrackAsync(PullRequestTarget? target, CancellationToken ct) {
		if (_tracking && _target == target) {
			return Task.CompletedTask;
		}

		_tracking = true;
		_target = target;
		return RefreshAsync(ct);
	}

	/// <summary>Re-reads the current PR, publishing only when something changed.</summary>
	public async Task RefreshAsync(CancellationToken ct) {
		await Gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await RefreshLockedAsync(ct).ConfigureAwait(false);
		} finally {
			Gate.Release();
		}
	}

	/// <summary>Loads <see cref="Target"/> and publishes the result; called holding <see cref="Gate"/>.</summary>
	protected abstract Task RefreshLockedAsync(CancellationToken ct);

	/// <summary>Records and publishes <paramref name="snapshot"/>; called holding <see cref="Gate"/>.</summary>
	protected void Publish(TSnapshot snapshot) {
		_latest = snapshot;
		_publish(snapshot);
	}
}
