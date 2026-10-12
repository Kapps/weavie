using Weavie.Core.Commands;

namespace Weavie.Core.Review;

/// <summary>Owns one session's PR overview: loads it for the branch's current PR and posts PR-level comments.</summary>
public sealed class PullRequestOverview : PullRequestOwner<PullRequestOverviewSnapshot> {
	private readonly IPullRequestDiscussion _discussion;

	/// <summary>Creates the owner, publishing every changed snapshot through <paramref name="publish"/>.</summary>
	public PullRequestOverview(IPullRequestDiscussion discussion, Action<PullRequestOverviewSnapshot> publish)
		: base(new PullRequestOverviewSnapshot(null, null), publish) {
		ArgumentNullException.ThrowIfNull(discussion);
		_discussion = discussion;
	}

	/// <summary>Posts a PR-level comment on PR <paramref name="number"/>; it never joins a pending review.</summary>
	public async Task<CommandResult> CommentAsync(int number, string body, CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(body)) {
			return CommandResult.Failure("A comment can't be empty.");
		}

		await Gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			if (Target is not { } target || Latest.Detail is not { } detail || target.Number != number || detail.Number != number) {
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
			Gate.Release();
		}
	}

	/// <inheritdoc/>
	protected override async Task RefreshLockedAsync(CancellationToken ct) {
		if (Target is not { } target) {
			Publish(new PullRequestOverviewSnapshot(null, null));
			return;
		}

		try {
			var detail = await _discussion.LoadDetailAsync(target.Repo, target.Number, ct).ConfigureAwait(false);
			if (Latest is not { Error: null, Detail: { } current } || current != detail) {
				Publish(new PullRequestOverviewSnapshot(detail, null));
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			var lastGood = Latest.Detail is { } detail && detail.Number == target.Number ? detail : null;
			Publish(new PullRequestOverviewSnapshot(lastGood, $"Couldn't load PR #{target.Number}: {ex.Message}"));
		}
	}
}
