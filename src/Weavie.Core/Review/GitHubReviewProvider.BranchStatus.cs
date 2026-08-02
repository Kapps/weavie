using System.Collections.Concurrent;
using System.Net;

namespace Weavie.Core.Review;

public sealed partial class GitHubReviewProvider {
	private readonly ConcurrentDictionary<BranchKey, BranchCache> _branchCache = new();

	/// <inheritdoc/>
	public async Task<PullRequestSummary?> FindForBranchAsync(
		RepoRef repo,
		string headOwner,
		string branch,
		CancellationToken ct = default) {
		ArgumentNullException.ThrowIfNull(repo);
		ArgumentException.ThrowIfNullOrWhiteSpace(headOwner);
		ArgumentException.ThrowIfNullOrWhiteSpace(branch);
		string token = await ResolveTokenAsync(ct).ConfigureAwait(false);
		string head = Uri.EscapeDataString($"{headOwner}:{branch}");
		return await FindForBranchStateAsync(repo, headOwner, branch, head, "open", token, ct)
			.ConfigureAwait(false)
			?? await FindForBranchStateAsync(repo, headOwner, branch, head, "closed", token, ct)
				.ConfigureAwait(false);
	}

	private async Task<PullRequestSummary?> FindForBranchStateAsync(
		RepoRef repo,
		string headOwner,
		string branch,
		string head,
		string state,
		string token,
		CancellationToken ct) {
		var key = new BranchKey(
			repo.Host.ToUpperInvariant(),
			repo.Owner.ToUpperInvariant(),
			repo.Name.ToUpperInvariant(),
			headOwner.ToUpperInvariant(),
			branch,
			state);
		_branchCache.TryGetValue(key, out var cached);
		using var request = BuildRequest(
			repo,
			HttpMethod.Get,
			$"/repos/{repo.Owner}/{repo.Name}/pulls?state={state}&head={head}&sort=updated&direction=desc&per_page=1",
			null,
			token);
		if (cached?.EntityTag is { Length: > 0 } entityTag) {
			request.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
		}

		using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.NotModified) {
			return cached is not null
				? cached.PullRequest
				: throw new InvalidOperationException(
					"GitHub returned an unchanged branch status without a cached response.");
		}

		string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		ThrowForFailure(response, repo);
		var result = ParsePullRequests(body).FirstOrDefault();
		_branchCache[key] = new BranchCache(response.Headers.ETag?.ToString(), result);
		return result;
	}

	private sealed record BranchKey(
		string Host,
		string Owner,
		string Repo,
		string HeadOwner,
		string Branch,
		string State);

	private sealed record BranchCache(string? EntityTag, PullRequestSummary? PullRequest);
}
