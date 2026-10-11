namespace Weavie.Core.Review;

// The overview half of the fake: each PR's seeded detail, with the viewer's PR-level comments appended as posted.
public sealed partial class StaticPullRequestProvider : IPullRequestDiscussion {
	/// <summary>Seeded overviews by PR number; a PR without one reads as its summary with no discussion or checks.</summary>
	public Dictionary<int, PullRequestDetail> Details { get; } = [];

	/// <inheritdoc/>
	public Task<PullRequestDetail> LoadDetailAsync(RepoRef repo, int number, CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		lock (_gate) {
			return Task.FromResult(Details.GetValueOrDefault(number) ?? (_pullRequests.FirstOrDefault(p => p.Number == number) is { } pr
				? new PullRequestDetail(PullRequestId(number), number, pr.Url, pr.Title, string.Empty, pr.IsDraft,
					new ForgeUser(pr.Author, string.Empty), string.Empty, [], [], [])
				: throw new InvalidOperationException($"GitHub has no PR #{number} in {repo.Owner}/{repo.Name}.")));
		}
	}

	/// <inheritdoc/>
	public async Task AddCommentAsync(RepoRef repo, string pullRequestId, string body, CancellationToken ct = default) {
		int number = _pullRequests.FirstOrDefault(p => PullRequestId(p.Number) == pullRequestId)?.Number
			?? throw new InvalidOperationException($"Could not resolve to a node with the global id of '{pullRequestId}'.");
		var detail = await LoadDetailAsync(repo, number, ct).ConfigureAwait(false);
		lock (_gate) {
			Details[number] = detail with {
				Conversation = [.. detail.Conversation, new PullRequestConversationItem($"comment-{++_nextId}", _viewer, body, null, Now(), string.Empty)],
			};
		}
	}
}
