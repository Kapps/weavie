using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// The review-diff surface: a session's worktree diffed against a base commit, walked in the editor's inline-diff
// navigator ("pr" mode). Fed by two producers — an opened pull request (HostCore.PullRequests.cs) and the local
// "diff against <ref>" command — over the same pr-changes / pr-diff messages. See docs/specs/diff-against.md.
public sealed partial class HostCore {
	// Each session's armed review, keyed by worktree path — stable across switches, unlike the session's
	// path-hashed Id, and unique per session. One review per session; arming a new one replaces the old.
	private readonly ConcurrentDictionary<string, DiffReview> _diffReviews = new(StringComparer.Ordinal);

	/// <summary>
	/// Arms a "diff against &lt;ref&gt;" review on the active session: resolves the ref to a commit, diffs the
	/// working tree from its merge-base with HEAD (so a branch shows only this side's changes), records the
	/// review, and pushes the changed-file list so the diff navigator surfaces. Failures surface as toasts;
	/// an empty diff says so and clears any prior review instead of arming an unwalkable navigator.
	/// </summary>
	private async Task DiffAgainstFromWebAsync(string reference) {
		reference = reference.Trim();
		if (reference.Length == 0 || _session is not { } session) {
			return;
		}

		string worktree = session.WorkspaceRoot;
		var git = new GitService();
		DiffReview review;
		IReadOnlyList<DiffFileChange> changes;
		try {
			if (await git.ResolveCommitAsync(worktree, reference, CancellationToken.None).ConfigureAwait(false) is not { } target) {
				Notify("error", $"'{reference}' isn't a branch, tag, or commit here.");
				return;
			}

			string head = await git.GetHeadCommitAsync(worktree, CancellationToken.None).ConfigureAwait(false);
			if (await git.MergeBaseAsync(worktree, target, head, CancellationToken.None).ConfigureAwait(false) is not { } mergeBase) {
				Notify("error", $"'{reference}' shares no history with HEAD — there's no base to diff from.");
				return;
			}

			review = new DiffReview(0, $"vs {reference}", string.Empty, mergeBase, head, null, worktree);
			changes = await ComputeReviewChangesAsync(review).ConfigureAwait(false);
		} catch (GitException ex) {
			Notify("error", $"Couldn't diff against '{reference}': {ex.Message}");
			return;
		}

		if (changes.Count == 0) {
			// Nothing to review: answer where the user is (a toast), and retract any prior review so a stale
			// walk can't sit under the "no changes" answer.
			Notify("info", $"No changes against '{reference}'.");
			if (_diffReviews.TryRemove(worktree, out _)) {
				PostReviewChanges(review, changes);
			}

			return;
		}

		_diffReviews[worktree] = review;
		PostReviewChanges(review, changes);
	}

	/// <summary>The changed-file list for <paramref name="review"/> — the file axis of the diff walk.</summary>
	private static Task<IReadOnlyList<DiffFileChange>> ComputeReviewChangesAsync(DiffReview review) =>
		// A PR diffs merge-base → its committed head; a local "diff against" diffs merge-base → the working
		// tree, so uncommitted edits are part of the review (its per-file "current" is the disk file either way).
		review.PrNumber > 0
			? new GitService().DiffRefsAsync(review.Worktree, review.MergeBase, review.HeadRef, CancellationToken.None)
			: new GitService().DiffWorktreeAsync(review.Worktree, review.MergeBase, CancellationToken.None);

	/// <summary>Computes and pushes <paramref name="review"/>'s changed-file list (<c>pr-changes</c>).</summary>
	private async Task PushReviewChangesAsync(DiffReview review) {
		IReadOnlyList<DiffFileChange> changes;
		try {
			changes = await ComputeReviewChangesAsync(review).ConfigureAwait(false);
		} catch (GitException ex) {
			Log($"[weavie] review '{review.Label}': diff failed: {ex.Message}");
			return;
		}

		PostReviewChanges(review, changes);
	}

	// Fire-and-forget producers can finish after a rapid session switch has moved off this review's session, and
	// the web applies pr-changes last-writer-wins with no guard. Guard and post ON the UI thread — where switches
	// run and in-order with their message train — so a stale diff can never check active, get preempted by a
	// switch, and still land after the incoming session's pushes.
	private void PostReviewChanges(DiffReview review, IReadOnlyList<DiffFileChange> changes) {
		_ui.Post(() => {
			if (_session is not { } active || !string.Equals(active.WorkspaceRoot, review.Worktree, StringComparison.Ordinal)) {
				return;
			}

			_bridge.PostToWeb(JsonSerializer.Serialize(new {
				type = "pr-changes",
				number = review.PrNumber,
				label = review.Label,
				files = changes.Select(c => new {
					path = Path.GetFullPath(Path.Combine(review.Worktree, c.Path)),
					name = Path.GetFileName(c.Path),
					added = c.Added,
					removed = c.Removed,
					line = 1,
				}),
			}));
		});
	}

	/// <summary>
	/// Answers <c>get-pr-diff</c> for one review file: its base→current pair (baseline = the file at the review's
	/// merge-base, current = the worktree file) plus any comments anchored in it, so the inline-diff renders it.
	/// </summary>
	private async Task SendReviewDiffAsync(int number, string absolutePath) {
		if (ActiveReview() is not { } review || review.PrNumber != number) {
			// Stale request (the session moved on) — dropping is correct, but log it: an unanswered get-pr-diff
			// strands the web's navigator mid-step, and this is the only trace of why.
			Log($"[weavie] review: dropped get-pr-diff #{number} for {Path.GetFileName(absolutePath)} (active: {ActiveReview()?.Label ?? "none"})");
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

		// Guard + post on the UI thread: a switch that landed while the git show / file read ran above means this
		// diff belongs to a session no longer on screen — drop it rather than render it over the wrong session.
		_ui.Post(() => {
			if (ActiveReview() is not { } stillActive || stillActive.PrNumber != number) {
				return;
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
		});
	}

	/// <summary>The review armed for the active session, or <c>null</c> when it has none.</summary>
	private DiffReview? ActiveReview() =>
		_session is { } session && _diffReviews.TryGetValue(session.WorkspaceRoot, out var review) ? review : null;

	/// <summary>Re-pushes the active session's review change list on a switch, so the navigator follows its session.</summary>
	private void PushActiveReviewChanges() {
		if (ActiveReview() is { } review) {
			_ = PushReviewChangesAsync(review);
		}
	}

	// A session's armed review: what feeding the navigator needs — the merge-base to diff against and the
	// worktree it's checked out in. A pull request (PrNumber > 0, HeadRef the committed head, Repo the forge
	// repo, Comments loaded) or a local "diff against <ref>" (PrNumber 0, no forge). Label names it in the UI.
	private sealed record DiffReview(int PrNumber, string Label, string HeadRef, string MergeBase, string HeadSha, RepoRef? Repo, string Worktree) {
		/// <summary>The review's forge comments, refreshed on arm and after each post; empty for a local ref diff.</summary>
		public List<ReviewComment> Comments { get; } = [];
	}
}
