using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<PullRequestWire[]> ListPullRequestsAsync(
		string query,
		CancellationToken ct) {
		if (await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is not { } repo) {
			throw new InvalidOperationException(
				"This workspace's 'origin' isn't a recognized GitHub repository.");
		}

		var pullRequests = string.IsNullOrWhiteSpace(query)
			? await _pullRequests.ListOpenAsync(repo, ct).ConfigureAwait(false)
			: await _pullRequests.SearchAsync(repo, query, ct).ConfigureAwait(false);
		return [.. pullRequests.Select(ToWire)];
	}

	private async Task<PullRequestWire?> GetPullRequestAsync(
		PullRequestReference request,
		CancellationToken ct) {
		if (request.Number <= 0
			|| await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is not { } repo
			|| IsForeign(request.Owner, request.Repo, repo)) {
			return null;
		}

		return await _pullRequests.GetAsync(repo, request.Number, ct).ConfigureAwait(false) is { } pullRequest
			? ToWire(pullRequest)
			: null;
	}

	private async Task<CommandResult> OpenPullRequestAsync(
		HostSession source,
		PullRequestReference request,
		CancellationToken ct) {
		if (request.Number <= 0) {
			return CommandResult.Failure("A pull request number is required.");
		}

		var repo = await ResolveOriginRepoAsync(ct).ConfigureAwait(false);
		if (repo is null) {
			return CommandResult.Failure(
				"This workspace's 'origin' isn't a recognized GitHub repository.");
		}

		if (IsForeign(request.Owner, request.Repo, repo)) {
			return CommandResult.Failure(
				$"PR #{request.Number} is in {request.Owner}/{request.Repo}, not "
				+ $"{repo.Owner}/{repo.Name}.");
		}

		PullRequestSummary? pullRequest;
		try {
			pullRequest = await _pullRequests
				.GetAsync(repo, request.Number, ct)
				.ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
			return CommandResult.Failure($"Couldn't open PR #{request.Number}: {ex.Message}");
		}

		if (pullRequest is null || string.IsNullOrWhiteSpace(pullRequest.HeadRef)) {
			return CommandResult.Failure(
				$"PR #{request.Number} wasn't found in {repo.Owner}/{repo.Name}.");
		}

		string headRef = pullRequest.HeadRef;
		if (!GitService.IsValidBranchName(headRef)) {
			return CommandResult.Failure(
				$"PR #{request.Number} has an unexpected branch name ('{headRef}').");
		}

		try {
			var git = new GitService();
			if (!await git.BranchExistsAsync(WorkspaceRoot, headRef, ct).ConfigureAwait(false)) {
				await git.FetchAsync(
					WorkspaceRoot,
					"origin",
					$"{headRef}:{headRef}",
					ct).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			return CommandResult.Failure(
				$"Couldn't fetch PR #{request.Number} ('{headRef}'): {ex.Message}");
		}

		var created = await NewSessionAsync(
			source.Address,
			new NewSessionRequest {
				Branch = headRef,
				Existing = true,
				Prompt = _settings.RequireBool(CoreSettings.PullRequestAutoReviewPrompt)
					? SeedPrompt(request.Number, pullRequest.Title, pullRequest.Url)
					: null,
			},
			ct).ConfigureAwait(false);
		if (!created.Ok) {
			return created;
		}

		if (_sessions?.Find(headRef)?.Session is not { } target) {
			return CommandResult.Failure(
				$"Opened PR #{request.Number}, but its session failed to load.");
		}

		string? reviewError = await ArmPrReviewAsync(
			target,
			request.Number,
			headRef,
			pullRequest.BaseRef,
			ct).ConfigureAwait(false);
		return reviewError is null
			? created
			: CommandResult.Failure(reviewError, created.DataJson);
	}

	private async Task<string?> ArmPrReviewAsync(
		HostSession session,
		int number,
		string headRef,
		string baseRef,
		CancellationToken ct) {
		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		string? mergeBase = null;
		try {
			if (GitService.IsValidBranchName(baseRef)) {
				await git.FetchAsync(WorkspaceRoot, "origin", baseRef, ct).ConfigureAwait(false);
				mergeBase = await git
					.MergeBaseAsync(worktree, $"origin/{baseRef}", headRef, ct)
					.ConfigureAwait(false)
					?? await git.MergeBaseAsync(worktree, baseRef, headRef, ct).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			Log($"[weavie] pr #{number}: couldn't resolve base '{baseRef}': {ex.Message}");
		}

		if (mergeBase is null) {
			return $"Opened PR #{number}, but couldn't compute its diff against '{baseRef}'.";
		}

		string headSha;
		try {
			headSha = await git.GetHeadCommitAsync(worktree, ct).ConfigureAwait(false);
		} catch (GitException) {
			headSha = headRef;
		}

		var repo = await ResolveOriginRepoAsync(ct).ConfigureAwait(false);
		var review = new DiffReview(number, $"PR #{number}", headRef, mergeBase, headSha, repo, worktree);
		await RefreshCommentsAsync(review, ct).ConfigureAwait(false);
		try {
			await SeedAndArmReviewAsync(
				review,
				session,
				await ComputeReviewChangesAsync(review, ct).ConfigureAwait(false),
				ct)
				.ConfigureAwait(false);
			return null;
		} catch (GitException ex) {
			return $"Opened PR #{number}, but couldn't compute its diff: {ex.Message}";
		}
	}

	private async Task<CommandResult> AddPrCommentAsync(
		HostSession session,
		ReviewCommentRequest request,
		CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(request.Body)
			|| ActiveReview(session) is not { } review
			|| review.PrNumber != request.Number
			|| review.Repo is not { } repo) {
			return CommandResult.Failure("That pull-request review is not active in this session.");
		}

		try {
			if (request.InReplyTo > 0) {
				await _reviewComments
					.ReplyAsync(repo, request.Number, request.InReplyTo, request.Body, ct)
					.ConfigureAwait(false);
			} else {
				string relative = Path
					.GetRelativePath(review.Worktree, request.Path)
					.Replace('\\', '/');
				string side = request.Side.Equals("left", StringComparison.OrdinalIgnoreCase)
					? "left"
					: "right";
				await _reviewComments.AddAsync(
					repo,
					request.Number,
					review.HeadSha,
					new NewReviewComment {
						Path = relative,
						Line = request.Line,
						Side = side,
						Body = request.Body,
					},
					ct).ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
			return CommandResult.Failure($"Couldn't post the comment: {ex.Message}");
		}

		await RefreshCommentsAsync(review, ct).ConfigureAwait(false);
		if (ReferenceEquals(ActiveReview(session), review)) {
			PushReviewCommentsToWeb(session, review, request.Path);
			PushTurnDiffToWeb(session, request.Path);
		}

		return CommandResult.Success();
	}

	private async Task RefreshCommentsAsync(DiffReview review, CancellationToken ct) {
		if (review.Repo is not { } repo) {
			return;
		}

		try {
			var comments = await _reviewComments
				.ListAsync(repo, review.PrNumber, ct)
				.ConfigureAwait(false);
			review.Comments.Clear();
			review.Comments.AddRange(comments);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
			Log($"[weavie] pr #{review.PrNumber}: couldn't load comments: {ex.Message}");
		}
	}

	private Task<RepoRef?> ResolveOriginRepoAsync(CancellationToken ct) =>
		ResolveRemoteRepoAsync("origin", ct);

	private async Task<RepoRef?> ResolveRemoteRepoAsync(string remote, CancellationToken ct) {
		try {
			string? url = await new GitService()
				.GetRemoteUrlAsync(WorkspaceRoot, remote, ct)
				.ConfigureAwait(false);
			return RepoRef.FromRemoteUrl(url);
		} catch (GitException) {
			return null;
		}
	}

	private static bool IsForeign(string owner, string name, RepoRef repo) =>
		!string.IsNullOrEmpty(owner)
		&& !string.IsNullOrEmpty(name)
		&& !(owner.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase)
			&& name.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));

	private static PullRequestWire ToWire(PullRequestSummary pullRequest) =>
		new(
			pullRequest.Number,
			pullRequest.Title,
			pullRequest.Author,
			pullRequest.HeadRef,
			pullRequest.Url,
			pullRequest.IsDraft);

	private static string SeedPrompt(int number, string title, string url) {
		string header = string.IsNullOrWhiteSpace(title) ? $"PR #{number}" : $"PR #{number}: {title}";
		string link = string.IsNullOrWhiteSpace(url) ? string.Empty : $"\n{url}";
		return $"You're checked out on the branch for {header}.{link}\n\n"
			+ "This is a REVIEW-ONLY session. Look over the changes on this branch and give me your review — "
			+ "what's good, what's risky, what could be improved. Do NOT edit, create, or delete any files, and do "
			+ "NOT run any commands that modify the branch, unless I explicitly ask you to make a change.";
	}

	private sealed record PullRequestReference(int Number, string Owner, string Repo);

	private sealed record PullRequestWire(
		int Number,
		string Title,
		string Author,
		string HeadRef,
		string Url,
		bool Draft);

	private sealed record ReviewCommentRequest(
		int Number,
		string Path,
		int Line,
		string Side,
		long InReplyTo,
		string Body);
}
