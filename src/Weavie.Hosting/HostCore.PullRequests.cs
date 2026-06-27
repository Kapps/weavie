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
	private async Task OpenPullRequestFromWebAsync(int number, string headRef, string title, string url) {
		if (number <= 0 || string.IsNullOrWhiteSpace(headRef)) {
			return;
		}

		if (!GitService.IsValidBranchName(headRef)) {
			Notify("error", $"PR #{number} has an unexpected branch name ('{headRef}').");
			return;
		}

		try {
			await new GitService().FetchAsync(WorkspaceRoot, "origin", headRef, CancellationToken.None).ConfigureAwait(false);
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
		}
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
			+ "Take a look at the changes on this branch and help me address any review feedback.";
	}
}
