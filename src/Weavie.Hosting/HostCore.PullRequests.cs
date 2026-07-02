using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

// The Open-PR flow: list a repo's open pull requests for the picker, and open one as a session checked out on
// its head branch. PR data comes from IPullRequestProvider (GitHub by default); the branch checkout reuses the
// existing attach-existing-worktree path. The overview tab + comments are later phases (see docs/specs/open-pr.md).
public sealed partial class HostCore {
	/// <summary>
	/// Answers the Open-PR picker: resolves the workspace's <c>origin</c> to a repo, lists its open PRs, and
	/// replies <c>prs-result</c> tagged by <paramref name="id"/>. A non-GitHub remote, a missing credential, or
	/// an API failure toasts and replies an empty list (so the picker never hangs).
	/// </summary>
	private async Task ListPullRequestsForWebAsync(string id, string query) {
		IReadOnlyList<PullRequestSummary> prs = [];
		if (await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false) is { } repo) {
			try {
				// Empty query → the recent-open default list; a typed query → forge-side search (scales past the
				// default without fetching everything).
				prs = string.IsNullOrWhiteSpace(query)
					? await _pullRequests.ListOpenAsync(repo, CancellationToken.None).ConfigureAwait(false)
					: await _pullRequests.SearchAsync(repo, query, CancellationToken.None).ConfigureAwait(false);
			} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
				Notify("error", $"Couldn't list pull requests: {ex.Message}");
			}
		} else {
			Notify("warn", "This workspace's 'origin' isn't a recognized GitHub repository.");
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "prs-result",
			id,
			prs = prs.Select(p => new {
				number = p.Number,
				title = p.Title,
				author = p.Author,
				headRef = p.HeadRef,
				url = p.Url,
				draft = p.IsDraft,
			}),
		}));
	}

	/// <summary>
	/// Resolves a typed <c>#N</c> / pasted URL to its PR for the picker's live preview, replying <c>pr-resolved</c>
	/// (tagged by <paramref name="id"/>) with the PR or <c>null</c> (not found, a foreign repo, or no credential).
	/// </summary>
	private async Task GetPullRequestForWebAsync(string id, int number, string owner, string repoName) {
		object? payload = null;
		if (number > 0 && await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false) is { } repo) {
			bool foreign = !string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repoName)
				&& !(owner.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase) && repoName.Equals(repo.Name, StringComparison.OrdinalIgnoreCase));
			if (!foreign) {
				try {
					if (await _pullRequests.GetAsync(repo, number, CancellationToken.None).ConfigureAwait(false) is { } pr) {
						payload = new { number = pr.Number, title = pr.Title, author = pr.Author, headRef = pr.HeadRef, url = pr.Url, draft = pr.IsDraft };
					}
				} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
					// Leave null — the preview just shows "not found"; opening surfaces the real error.
				}
			}
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "pr-resolved", id, pr = payload }));
	}

	/// <summary>
	/// Opens a PR as a session: fetches its head branch from <c>origin</c>, then checks it out into a worktree
	/// session (reusing the attach-existing path, which de-dupes to a live session if one already exists), seeding
	/// Claude's first message with the PR's context. Any failure surfaces as a toast.
	/// </summary>
	private async Task OpenPullRequestFromWebAsync(int number, string owner, string repoName) {
		if (number <= 0) {
			return;
		}

		var repo = await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false);
		if (repo is null) {
			Notify("warn", "This workspace's 'origin' isn't a recognized GitHub repository.");
			return;
		}

		// A pasted URL carries its own owner/repo; refuse one that isn't this workspace's origin (its branch
		// wouldn't be fetchable here). A typed #N / a picked result sends no owner/repo and targets origin.
		if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(repoName)
			&& !(owner.Equals(repo.Owner, StringComparison.OrdinalIgnoreCase) && repoName.Equals(repo.Name, StringComparison.OrdinalIgnoreCase))) {
			Notify("warn", $"PR #{number} is in {owner}/{repoName}, not this workspace's repository ({repo.Owner}/{repo.Name}).");
			return;
		}

		PullRequestSummary? pr;
		try {
			pr = await _pullRequests.GetAsync(repo, number, CancellationToken.None).ConfigureAwait(false);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
			Notify("error", $"Couldn't open PR #{number}: {ex.Message}");
			return;
		}

		if (pr is null || string.IsNullOrWhiteSpace(pr.HeadRef)) {
			Notify("error", $"PR #{number} wasn't found in {repo.Owner}/{repo.Name}.");
			return;
		}

		string headRef = pr.HeadRef;
		string baseRef = pr.BaseRef;
		string title = pr.Title;
		string url = pr.Url;
		if (!GitService.IsValidBranchName(headRef)) {
			Notify("error", $"PR #{number} has an unexpected branch name ('{headRef}').");
			return;
		}

		try {
			var git = new GitService();
			// A bare `git fetch origin <ref>` leaves only FETCH_HEAD + origin/<ref>, not a local branch — and the
			// worktree checkout needs `refs/heads/<ref>`. So fetch the PR head into a *local* branch (the `<ref>:<ref>`
			// refspec, both sides built from the validated name). Skip it when the branch already exists locally, so
			// reopening a PR never clobbers local commits on it.
			if (!await git.BranchExistsAsync(WorkspaceRoot, headRef, CancellationToken.None).ConfigureAwait(false)) {
				await git.FetchAsync(WorkspaceRoot, "origin", $"{headRef}:{headRef}", CancellationToken.None).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			Notify("error", $"Couldn't fetch PR #{number} ('{headRef}'): {ex.Message}");
			return;
		}

		var result = await NewSessionAsync(
			new NewSessionRequest {
				Branch = headRef,
				AttachExisting = true,
				Prompt = _settings.GetBool("pr.autoReviewPrompt", fallback: true) ? SeedPrompt(number, title, url) : null,
			},
			CancellationToken.None).ConfigureAwait(false);
		if (!result.Ok) {
			Notify("error", result.Error ?? $"Couldn't open PR #{number}.");
			return;
		}

		await ArmPrReviewAsync(number, headRef, baseRef, title, url).ConfigureAwait(false);
	}

	// Each PR session's review state, keyed by session id (= the head branch). Holds what computing a per-file
	// diff needs: the merge-base to diff against and the worktree the head is checked out in.
	private readonly ConcurrentDictionary<string, PullRequestReview> _prReviews = new(StringComparer.Ordinal);

	/// <summary>
	/// Arms PR review on the just-opened session: fetches the base, computes the merge-base, records the review,
	/// and pushes the changed-file list so the diff navigator surfaces. A diff failure toasts and leaves the
	/// session usable (the checkout still succeeded).
	/// </summary>
	private async Task ArmPrReviewAsync(int number, string headRef, string baseRef, string title, string url) {
		// The just-opened PR is the active session (attach switched to it). Key the review by the worktree path —
		// stable across switches, unlike the session's path-hashed Id, and unique per session.
		if (_session is not { } session) {
			return;
		}

		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		string? mergeBase = null;
		try {
			if (GitService.IsValidBranchName(baseRef)) {
				await git.FetchAsync(WorkspaceRoot, "origin", baseRef, CancellationToken.None).ConfigureAwait(false);
				mergeBase = await git.MergeBaseAsync(worktree, $"origin/{baseRef}", headRef, CancellationToken.None).ConfigureAwait(false)
					?? await git.MergeBaseAsync(worktree, baseRef, headRef, CancellationToken.None).ConfigureAwait(false);
			}
		} catch (GitException ex) {
			Log($"[weavie] pr #{number}: couldn't resolve base '{baseRef}': {ex.Message}");
		}

		if (mergeBase is null) {
			Notify("warn", $"Opened PR #{number}, but couldn't compute its diff against '{baseRef}'.");
			return;
		}

		string headSha;
		try {
			headSha = await git.GetHeadCommitAsync(worktree, CancellationToken.None).ConfigureAwait(false);
		} catch (GitException) {
			headSha = headRef;
		}

		var repo = await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false);
		var review = new PullRequestReview(number, title, url, baseRef, headRef, mergeBase, headSha, repo, worktree);
		_prReviews[worktree] = review;
		await RefreshCommentsAsync(review).ConfigureAwait(false);
		await PushPrChangesAsync(review).ConfigureAwait(false);
	}

	/// <summary>Re-loads a PR's review comments into the review (best-effort; a forge error leaves the prior set).</summary>
	private async Task RefreshCommentsAsync(PullRequestReview review) {
		if (review.Repo is not { } repo) {
			return;
		}

		try {
			var comments = await _reviewComments.ListAsync(repo, review.Number, CancellationToken.None).ConfigureAwait(false);
			review.Comments.Clear();
			review.Comments.AddRange(comments);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
			Log($"[weavie] pr #{review.Number}: couldn't load comments: {ex.Message}");
		}
	}

	/// <summary>Computes and pushes a PR's changed-file list (<c>pr-changes</c>) — the file axis of the diff walk.</summary>
	private async Task PushPrChangesAsync(PullRequestReview review) {
		IReadOnlyList<DiffFileChange> changes;
		try {
			changes = await new GitService().DiffRefsAsync(review.Worktree, review.MergeBase, review.HeadRef, CancellationToken.None).ConfigureAwait(false);
		} catch (GitException ex) {
			Log($"[weavie] pr #{review.Number}: diff failed: {ex.Message}");
			return;
		}

		// Fire-and-forget: this diff can finish after a rapid session switch has moved off the PR. The web applies
		// pr-changes last-writer-wins with no guard, so a stale result would clobber the now-active session's
		// file list — or, landing out of order, re-park its navigator. Drop it unless this PR is still active.
		if (_session is not { } active || !string.Equals(active.WorkspaceRoot, review.Worktree, StringComparison.Ordinal)) {
			return;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "pr-changes",
			number = review.Number,
			files = changes.Select(c => new {
				path = Path.GetFullPath(Path.Combine(review.Worktree, c.Path)),
				name = Path.GetFileName(c.Path),
				added = c.Added,
				removed = c.Removed,
				line = 1,
			}),
		}));
	}

	/// <summary>
	/// Answers <c>get-pr-diff</c> for one file: its base→head pair (baseline = the file at the merge-base, current
	/// = the worktree file) so the inline-diff renderer can show it. Comments arrive in a later phase.
	/// </summary>
	private async Task SendPrDiffAsync(int number, string absolutePath) {
		if (ActivePrReview() is not { } review || review.Number != number) {
			// Stale request (the session moved on) — dropping is correct, but log it: an unanswered get-pr-diff
			// strands the web's navigator mid-step, and this is the only trace of why.
			Log($"[weavie] pr #{number}: dropped get-pr-diff for {Path.GetFileName(absolutePath)} (active pr: {ActivePrReview()?.Number.ToString() ?? "none"})");
			return;
		}

		string relative = Path.GetRelativePath(review.Worktree, absolutePath).Replace('\\', '/');
		string baseline;
		try {
			baseline = await new GitService().ShowFileAtRefAsync(review.Worktree, review.MergeBase, relative, CancellationToken.None).ConfigureAwait(false);
		} catch (GitException) {
			baseline = string.Empty;
		}

		string current;
		try {
			current = File.Exists(absolutePath) ? await File.ReadAllTextAsync(absolutePath).ConfigureAwait(false) : string.Empty;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			current = string.Empty;
		}

		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "pr-diff",
			number,
			path = absolutePath,
			name = Path.GetFileName(absolutePath),
			baseline,
			current,
			comments = review.Comments
				.Where(c => string.Equals(c.Path, relative, StringComparison.Ordinal))
				.Select(c => new {
					id = c.Id,
					line = c.Line,
					side = c.Side,
					author = c.Author,
					body = c.Body,
					createdAt = c.CreatedAt,
					inReplyTo = c.InReplyTo,
				}),
		}));
	}

	/// <summary>
	/// Posts a review comment (or a reply) on the active PR, then re-loads the thread and re-renders the file's
	/// diff so the new comment appears. A non-reply needs the file path + line + side; a reply needs the parent
	/// <paramref name="inReplyTo"/>. Failure toasts and keeps the draft (the web doesn't clear it until success).
	/// </summary>
	private async Task AddPrCommentFromWebAsync(int number, string absolutePath, int line, string side, long inReplyTo, string body) {
		if (string.IsNullOrWhiteSpace(body) || ActivePrReview() is not { } review || review.Number != number || review.Repo is not { } repo) {
			return;
		}

		try {
			if (inReplyTo > 0) {
				await _reviewComments.ReplyAsync(repo, number, inReplyTo, body, CancellationToken.None).ConfigureAwait(false);
			} else {
				string relative = Path.GetRelativePath(review.Worktree, absolutePath).Replace('\\', '/');
				string resolvedSide = side.Equals("left", StringComparison.OrdinalIgnoreCase) ? "left" : "right";
				await _reviewComments.AddAsync(
					repo, number, review.HeadSha,
					new NewReviewComment { Path = relative, Line = line, Side = resolvedSide, Body = body }, CancellationToken.None).ConfigureAwait(false);
			}
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException) {
			Notify("error", $"Couldn't post the comment: {ex.Message}");
			return;
		}

		await RefreshCommentsAsync(review).ConfigureAwait(false);
		await SendPrDiffAsync(number, absolutePath).ConfigureAwait(false);
	}

	/// <summary>The PR review armed for the active session, or <c>null</c> when the active session isn't a PR.</summary>
	private PullRequestReview? ActivePrReview() =>
		_session is { } session && _prReviews.TryGetValue(session.WorkspaceRoot, out var review) ? review : null;

	/// <summary>Re-pushes the active session's PR change list on a switch, so the navigator follows the PR session.</summary>
	private void PushActivePrChanges() {
		if (ActivePrReview() is { } review) {
			_ = PushPrChangesAsync(review);
		}
	}

	private sealed record PullRequestReview(
		int Number, string Title, string Url, string BaseRef, string HeadRef, string MergeBase, string HeadSha, RepoRef? Repo, string Worktree) {
		/// <summary>The PR's review comments, refreshed on arm and after each post.</summary>
		public List<ReviewComment> Comments { get; } = [];
	}

	/// <summary>Resolves the workspace's <c>origin</c> remote URL to a <see cref="RepoRef"/>, or <c>null</c> when it isn't a forge repo.</summary>
	private async Task<RepoRef?> ResolveOriginRepoAsync(CancellationToken ct) {
		try {
			string? url = await new GitService().GetRemoteUrlAsync(WorkspaceRoot, "origin", ct).ConfigureAwait(false);
			return RepoRef.FromRemoteUrl(url);
		} catch (GitException) {
			return null;
		}
	}

	private static string SeedPrompt(int number, string title, string url) {
		string header = string.IsNullOrWhiteSpace(title) ? $"PR #{number}" : $"PR #{number}: {title}";
		string link = string.IsNullOrWhiteSpace(url) ? string.Empty : $"\n{url}";
		return $"You're checked out on the branch for {header}.{link}\n\n"
			+ "This is a REVIEW-ONLY session. Look over the changes on this branch and give me your review — "
			+ "what's good, what's risky, what could be improved. Do NOT edit, create, or delete any files, and do "
			+ "NOT run any commands that modify the branch, unless I explicitly ask you to make a change.";
	}
}
