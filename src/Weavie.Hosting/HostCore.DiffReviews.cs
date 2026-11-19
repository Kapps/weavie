using System.Text.Json.Serialization;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// A git review: the session's worktree compared with a base commit, reviewed through the same inline keep/revert
// engine as a turn (HostCore.WebBridge.cs). Fed by an opened pull request (HostCore.PullRequests.cs) and the local
// "diff against <ref>" command; every start and re-list replaces the review with git's answer. See
// docs/specs/review-state.md.
public sealed partial class HostCore {

	/// <summary>
	/// Starts a "diff against &lt;ref&gt;" review on its owning session: resolves the ref, then lists the working tree
	/// against its merge-base with HEAD (so a branch shows only this side's changes), replacing whatever review was
	/// open. Failures surface as toasts. Answers whether a new review opened; the page shows its overview only if the
	/// user hasn't navigated since asking.
	/// </summary>
	private async Task<ReviewOpened> DiffAgainstFromWebAsync(HostSession session, GitService git, string reference, CancellationToken ct) {
		reference = reference.Trim();
		if (reference.Length == 0) {
			return ReviewOpened.No;
		}

		string worktree = session.WorkspaceRoot;
		ReviewContext? review = null;
		bool resuming = false;
		try {
			bool replaced = await StartReviewAsync(session, async token => {
				if (await git.ResolveCommitAsync(worktree, reference, token).ConfigureAwait(false) is not { } target) {
					Notify(session, "warn", $"'{reference}' isn't a branch, tag, or commit here.");
					return null;
				}

				if (await git.MergeBaseAsync(worktree, target, "HEAD", token).ConfigureAwait(false) is not { } mergeBase) {
					Notify(session, "warn", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
					return null;
				}

				string branch = await git.GetCurrentBranchAsync(worktree, token).ConfigureAwait(false) ?? string.Empty;
				review = new ReviewContext(0, $"vs {reference}", mergeBase, null, worktree, reference, branch);
				resuming = session.Changes.Review is { } open && open.SameSource(review);
				return review;
			}, ct).ConfigureAwait(false);
			return replaced ? new(resuming ? ReviewOutcome.Resumed : ReviewOutcome.Opened, review!.Label) : ReviewOpened.No;
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			Notify(session, "warn", $"Couldn't diff against '{reference}': {ex.Message}");
			return ReviewOpened.No;
		}
	}

	/// <summary>
	/// Diffs against a ref the user named; once that review is open, a branch becomes the branch's review base (see
	/// <see cref="GitService.ChooseBranchBaseAsync"/>), so Review Branch keeps comparing with it. That runs after the
	/// answer: the page reveals the review only if the user hasn't navigated, so a late answer would override them.
	/// </summary>
	private async Task<ReviewOpened> ChooseDiffAgainstFromWebAsync(HostSession session, string reference, CancellationToken ct) {
		var git = GitService.ForOperation();
		var result = await DiffAgainstFromWebAsync(session, git, reference, ct).ConfigureAwait(false);
		if (result.Outcome != ReviewOutcome.None && session.Changes.Review?.Label == result.Label) {
			_ = session.Background.Run(stopping => ChooseBranchBaseAsync(session, git, reference.Trim(), stopping));
		}

		return result;
	}

	private async Task ChooseBranchBaseAsync(HostSession session, GitService git, string chosen, CancellationToken ct) {
		try {
			if (await DefaultBranchRefAsync(session.WorkspaceRoot, git, ct).ConfigureAwait(false) is { } preferred
				&& await git.ChooseBranchBaseAsync(
					session.WorkspaceRoot, chosen, preferred, PullRequestBase(session), ct).ConfigureAwait(false)) {
				Notify(session, "info", $"Review Branch now compares with '{chosen}'.");
			}
		} catch (GitException ex) {
			Notify(session, "warn", $"Couldn't remember '{chosen}' as this branch's review base: {ex.Message}");
		}
	}

	/// <summary>Reviews the whole branch: a diff against <see cref="BranchBaseRefAsync"/> from their merge-base.</summary>
	private async Task<ReviewOpened> DiffAgainstBaseFromWebAsync(HostSession session, CancellationToken ct) {
		var git = GitService.ForOperation();
		if (await BranchBaseRefAsync(session, git, ct).ConfigureAwait(false) is not { } reference) {
			Notify(session, "warn", "This repository has no default branch (origin/HEAD, main, or master) to review against.");
			return ReviewOpened.No;
		}

		return await DiffAgainstFromWebAsync(session, git, reference, ct).ConfigureAwait(false);
	}

	/// <summary>The ref a whole-branch review compares against (see <see cref="GitService.ResolveBranchBaseAsync"/>).</summary>
	private static async Task<string?> BranchBaseRefAsync(HostSession session, GitService git, CancellationToken ct) =>
		await DefaultBranchRefAsync(session.WorkspaceRoot, git, ct).ConfigureAwait(false) is { } preferred
			? await git.ResolveBranchBaseAsync(
				session.WorkspaceRoot, preferred, PullRequestBase(session), ct).ConfigureAwait(false)
			: null;

	/// <summary>
	/// The remote's default branch, else the local one. The remote's copy wins because a local default branch is often
	/// far behind and would fold others' merged work into the diff.
	/// </summary>
	private static async Task<string?> DefaultBranchRefAsync(string worktree, GitService git, CancellationToken ct) {
		if (await git.ResolveDefaultBranchAsync(worktree, ct).ConfigureAwait(false) is not { } branch) {
			return null;
		}

		string remote = $"origin/{branch}";
		return await git.ResolveCommitAsync(worktree, remote, ct).ConfigureAwait(false) is null ? branch : remote;
	}

	/// <summary>The remote-tracking ref of the branch the session's open pull request targets, if it has one.</summary>
	private static string? PullRequestBase(HostSession session) =>
		session.PullRequestStatus.Latest is { PullRequest.State: PullRequestStatusInfo.Open, Target: { } target }
			? $"{target.Remote}/{target.BaseRef}"
			: null;

	/// <summary>
	/// Starts a review the user asked for: <paramref name="resolve"/> finds its base (null when it toasted why it can't),
	/// then it is listed — again from the top whenever HEAD moved while git was read, so it never shows a mix of two
	/// commits. Answers whether the session's review was replaced.
	/// </summary>
	private async Task<bool> StartReviewAsync(
		HostSession session, Func<CancellationToken, Task<ReviewContext?>> resolve, CancellationToken ct) {
		var request = session.Changes.BeginReviewRequest();
		try {
			while (true) {
				var start = session.ReviewListing.Observe();
				if (await resolve(ct).ConfigureAwait(false) is not { } review) return false;
				var listed = await ListReviewAsync(session, review, request, start, ct).ConfigureAwait(false);
				if (listed != Listed.HeadMoved) return listed == Listed.Replaced;
			}
		} finally {
			EndListing(session, request);
		}
	}

	// A released request may have held off a re-list, so a review still stale is followed again.
	private static void EndListing(HostSession session, ReviewRequest request) {
		session.Changes.EndReviewRequest(request);
		if (session.ReviewListing.Stale) session.ReviewHead.RequestRefresh();
	}

	/// <summary>
	/// Lists <paramref name="review"/> from git — the working tree's changed paths against its merge-base, each before at
	/// that base, each current from disk — and replaces the session's review with it unless <paramref name="request"/>
	/// was superseded or HEAD moved since <paramref name="start"/>, before the base was resolved. An empty listing closes
	/// a review of the same source and toasts. Paths joined while git was read are joined again and published.
	/// </summary>
	private async Task<Listed> ListReviewAsync(
		HostSession session, ReviewContext review, ReviewRequest request, ListingState start, CancellationToken ct) {
		bool finished = false;
		try {
			var git = new GitService();
			var changes = await git.DiffWorktreeAsync(review.Worktree, review.MergeBase, ct).ConfigureAwait(false);
			var befores = await git
				.ReadFilesAtRefsAsync(review.Worktree, [.. changes.Select(change => (review.MergeBase, change.Path))], ct)
				.ConfigureAwait(false);
			var seeds = new List<ReviewSeed>(changes.Count);
			for (int i = 0; i < changes.Count; i++) {
				string absolute = Path.GetFullPath(Path.Combine(review.Worktree, changes[i].Path));
				bool exists = session.FileSystem.FileExists(absolute);
				string current = string.Empty;
				// Binary files aren't reviewed as text, the same as an agent's binary edit.
				if (exists && !session.FileSystem.TryReadAllText(absolute, out current)) continue;
				seeds.Add(new ReviewSeed(absolute, befores[i].Content, current, befores[i].Exists, exists));
			}

			bool moved = session.ReviewListing.HeadMovedSince(start);
			// Finished before the replace announces the review, so following it never re-lists what was just read.
			session.ReviewListing.Finish(start, !moved);
			finished = true;
			if (moved) return Listed.HeadMoved;

			var joined = await _ui.InvokeAsync(() => Task.FromResult(ReplaceReviewOnUi(session, review, [.. seeds], request)), ct)
				.ConfigureAwait(false);
			if (joined is null) return Listed.NotReplaced;
			if (joined.Count > 0) _ = session.Background.Run(stopping => JoinAndRefreshReviewAsync(session, joined, stopping));
			return Listed.Replaced;
		} finally {
			// A failed listing settles what it set out to cover, so a persistent failure isn't retried in a loop.
			if (!finished) session.ReviewListing.Finish(start, true);
		}
	}

	private IReadOnlyCollection<string>? ReplaceReviewOnUi(
		HostSession session, ReviewContext review, ReviewSeed[] seeds, ReviewRequest request) {
		string[] prior = [.. session.Changes.TurnChanges().Select(change => change.Path)];
		// An empty listing closes a review of the same source, unless a file joined while git was read.
		if (seeds.Length == 0 && !(session.Changes.Review is { } open && open.SameSource(review))) {
			Notify(session, "info", $"No changes against '{review.Target}'.");
			return null;
		}

		IReadOnlyCollection<string>? joined;
		try {
			joined = session.Changes.ReplaceReview(review, seeds, request);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Replacing may have changed what the page holds revisions of before it failed.
			PushReviewStateToWeb(session, session.Bus.BroadcastTarget);
			throw;
		}
		if (joined is null) return null;
		if (seeds.Length == 0 && joined.Count == 0) {
			session.Changes.CloseReview();
			PushReviewActionToWeb(session, []);
			Notify(session, "info", $"No changes against '{review.Target}'.");
			return null;
		}
		PushTurnChangesToWeb(session);
		PushReviewHistoryToWeb(session);
		foreach (string path in prior.Union(session.Changes.TurnChanges().Select(change => change.Path), PathIdentity.Comparer))
			PushTurnDiffToWeb(session, path);
		return [.. joined];
	}

	/// <summary>
	/// Brings changed files into the open git review at its base: a held file refreshes from disk with no git call, and
	/// new ones read their befores in one git call. A review replaced meanwhile is joined instead.
	/// </summary>
	private static async Task JoinReviewAsync(HostSession session, IReadOnlyCollection<string> paths, CancellationToken ct) {
		var git = new GitService();
		var remaining = paths;
		while (remaining.Count > 0 && session.Changes.Review is { } review) {
			string[] unheld = [.. remaining.Where(path => PathBoundary.Contains(review.Worktree, path)
				&& !session.Changes.RefreshReviewPath(review, path))];
			if (unheld.Length == 0) return;
			var befores = await git.ReadFilesAtRefsAsync(review.Worktree, [.. unheld.Select(path =>
				(review.MergeBase, Path.GetRelativePath(review.Worktree, path).Replace(Path.DirectorySeparatorChar, '/')))], ct)
				.ConfigureAwait(false);
			remaining = [.. session.Changes.JoinReview(review, [.. unheld.Select((path, i) => new ReviewJoin(path, befores[i].Content, befores[i].Exists))])
				.Select(join => join.Path)];
		}
	}

	private static ReviewContext? ActiveReview(HostSession session) => session.Changes.Review;
}

/// <summary>What one listing did with the session's review.</summary>
internal enum Listed {
	Replaced,
	NotReplaced,

	/// <summary>HEAD moved while git was read, so nothing was installed.</summary>
	HeadMoved,
}

/// <summary>What a request did with its review <c>Label</c>.</summary>
internal sealed record ReviewOpened(ReviewOutcome Outcome, string Label) {
	public static ReviewOpened No { get; } = new(ReviewOutcome.None, string.Empty);
}

/// <summary>Whether a review request armed a new review, resumed the one already open, or showed nothing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewOutcome>))]
internal enum ReviewOutcome {
	[JsonStringEnumMemberName("none")]
	None,

	[JsonStringEnumMemberName("opened")]
	Opened,

	[JsonStringEnumMemberName("resumed")]
	Resumed,
}
