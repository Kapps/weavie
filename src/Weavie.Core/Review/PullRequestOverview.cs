using Weavie.Core.Commands;

namespace Weavie.Core.Review;

/// <summary>
/// Owns one session's PR overview: loads it for the branch's current PR, posts PR-level comments, and publishes each
/// change. Loads and posts run behind one gate and publish inside it, so the last published snapshot is the newest.
/// </summary>
public sealed class PullRequestOverview {
	private readonly SemaphoreSlim _gate = new(1, 1);
	private readonly IPullRequestDiscussion _discussion;
	private readonly Action<PullRequestOverviewSnapshot> _publish;
	private volatile PullRequestTarget? _target;
	private volatile bool _tracking;
	private volatile PullRequestOverviewSnapshot _latest = new(null, null);

	/// <summary>Creates the owner, publishing every changed snapshot through <paramref name="publish"/>.</summary>
	public PullRequestOverview(IPullRequestDiscussion discussion, Action<PullRequestOverviewSnapshot> publish) {
		ArgumentNullException.ThrowIfNull(discussion);
		ArgumentNullException.ThrowIfNull(publish);
		_discussion = discussion;
		_publish = publish;
	}

	/// <summary>The most recently published snapshot.</summary>
	public PullRequestOverviewSnapshot Latest => _latest;

	/// <summary>Follows the branch's PR (null: none), reloading when the PR or its head changes.</summary>
	public Task TrackAsync(PullRequestTarget? target, CancellationToken ct) {
		if (_tracking && _target == target) {
			return Task.CompletedTask;
		}

		_tracking = true;
		_target = target;
		return RefreshAsync(ct);
	}

	/// <summary>Re-reads the current PR's overview, publishing only when something changed.</summary>
	public async Task RefreshAsync(CancellationToken ct) {
		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await RefreshLockedAsync(ct).ConfigureAwait(false);
		} finally {
			_gate.Release();
		}
	}

	/// <summary>Posts a PR-level comment on PR <paramref name="number"/>; it never joins a pending review.</summary>
	public async Task<CommandResult> CommentAsync(int number, string body, CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(body)) {
			return CommandResult.Failure("A comment can't be empty.");
		}

		await _gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			if (_target is not { } target || _latest.Detail is not { } detail || target.Number != number || detail.Number != number) {
				return CommandResult.Failure($"PR #{number} isn't this session's pull request.");
			}

			try {
				await _discussion.AddCommentAsync(target.Repo, detail.Id, body, ct).ConfigureAwait(false);
			} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
				return CommandResult.Failure(ex.Message);
			} finally {
				await RefreshLockedAsync(ct).ConfigureAwait(false);
			}

			return CommandResult.Success();
		} finally {
			_gate.Release();
		}
	}

	private async Task RefreshLockedAsync(CancellationToken ct) {
		if (_target is not { } target) {
			Publish(new PullRequestOverviewSnapshot(null, null));
			return;
		}

		try {
			var detail = await _discussion.LoadDetailAsync(target.Repo, target.Number, ct).ConfigureAwait(false);
			if (_latest is not { Error: null, Detail: { } current } || current != detail) {
				Publish(new PullRequestOverviewSnapshot(detail, null));
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			var lastGood = _latest.Detail is { } detail && detail.Number == target.Number ? detail : null;
			Publish(new PullRequestOverviewSnapshot(lastGood, $"Couldn't load PR #{target.Number}: {ex.Message}"));
		}
	}

	private void Publish(PullRequestOverviewSnapshot snapshot) {
		_latest = snapshot;
		_publish(snapshot);
	}
}
