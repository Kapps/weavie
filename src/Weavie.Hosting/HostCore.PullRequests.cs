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
	private async Task ListPullRequestsForWebAsync(string id) {
		IReadOnlyList<PullRequestSummary> prs = [];
		if (await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false) is { } repo) {
			try {
				prs = await _pullRequests.ListOpenAsync(repo, CancellationToken.None).ConfigureAwait(false);
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
				baseRef = p.BaseRef,
				url = p.Url,
				draft = p.IsDraft,
			}),
		}));
	}

	/// <summary>
	/// Opens a PR as a session: fetches its head branch from <c>origin</c>, then checks it out into a worktree
	/// session (reusing the attach-existing path, which de-dupes to a live session if one already exists), seeding
	/// Claude's first message with the PR's context. Any failure surfaces as a toast.
	/// </summary>
	private async Task OpenPullRequestFromWebAsync(int number, string headRef, string baseRef, string title, string url) {
		if (number <= 0 || string.IsNullOrWhiteSpace(headRef)) {
			return;
		}

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
				Prompt = SeedPrompt(number, title, url),
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

		var review = new PullRequestReview(number, title, url, baseRef, headRef, mergeBase, worktree);
		_prReviews[worktree] = review;
		await PushPrChangesAsync(review).ConfigureAwait(false);
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
			comments = Array.Empty<object>(),
		}));
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
		int Number, string Title, string Url, string BaseRef, string HeadRef, string MergeBase, string Worktree);

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
			+ "Take a look at the changes on this branch and help me address any review feedback.";
	}
}
