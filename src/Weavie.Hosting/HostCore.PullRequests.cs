using System.Diagnostics;
using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<PullRequestWire[]> ListPullRequestsAsync(
		HostSession session,
		string query,
		CancellationToken ct) {
		if (await ResolveOriginRepoAsync(session, ct).ConfigureAwait(false) is not { } repo) {
			throw new InvalidOperationException(NoGitHubOrigin(session));
		}

		var pullRequests = string.IsNullOrWhiteSpace(query)
			? await _pullRequests.ListOpenAsync(repo, ct).ConfigureAwait(false)
			: await _pullRequests.SearchAsync(repo, query, ct).ConfigureAwait(false);
		return [.. pullRequests.Select(ToWire)];
	}

	private async Task<PullRequestWire?> GetPullRequestAsync(
		HostSession session,
		PullRequestReference request,
		CancellationToken ct) {
		if (request.Number <= 0
			|| await ResolveOriginRepoAsync(session, ct).ConfigureAwait(false) is not { } repo
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

		long started = Stopwatch.GetTimestamp();
		void Step(string step) =>
			Console.WriteLine($"[pr-open] #{request.Number} {step} +{Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");

		var repo = await ResolveOriginRepoAsync(source, ct).ConfigureAwait(false);
		if (repo is null) {
			return CommandResult.Failure(NoGitHubOrigin(source));
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

		Step("found on GitHub");
		string headRef = pullRequest.HeadRef;
		if (!GitService.IsValidBranchName(headRef)) {
			return CommandResult.Failure(
				$"PR #{request.Number} has an unexpected branch name ('{headRef}').");
		}

		bool opensNewSession;
		try {
			var git = new GitService();
			if (!await git.BranchExistsAsync(source.Repo.Root, headRef, ct).ConfigureAwait(false)) {
				await git.FetchCommitAsync(source.Repo.Root, "origin", pullRequest.HeadSha, ct).ConfigureAwait(false);
				await git.CreateBranchAsync(source.Repo.Root, headRef, pullRequest.HeadSha, ct).ConfigureAwait(false);
			}

			opensNewSession = await SlotOnBranchAsync(source.Repo, headRef, ct).ConfigureAwait(false) is null;
		} catch (GitException ex) {
			return CommandResult.Failure(
				$"Couldn't fetch PR #{request.Number} ('{headRef}'): {ex.Message}");
		}

		Step("branch ready");
		var created = await NewSessionAsync(
			source.Address,
			new NewSessionRequest {
				Branch = headRef,
				Existing = true,
				Prompt = opensNewSession && _settings.RequireBool(CoreSettings.PullRequestAutoReviewPrompt)
					? SeedPrompt(request.Number, pullRequest.Title, pullRequest.Url)
					: null,
			},
			SourceDevice,
			ct).ConfigureAwait(false);
		if (!created.Ok) {
			return created;
		}

		// The session the open answered with is the one reviewed, whatever its label says.
		var opened = JsonSerializer.Deserialize(created.DataJson ?? "null", WireJson.Default.SessionActivation);
		if (opened is null || _sessions?.Find(opened.Id)?.Session is not { } target) {
			return CommandResult.Failure(
				$"Opened PR #{request.Number}, but its session failed to load.");
		}

		Step("session created");
		string? reviewError = await ArmPrReviewAsync(
			target,
			request.Number,
			headRef,
			pullRequest.BaseRef,
			ct).ConfigureAwait(false);
		Step("review armed");
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
		var repo = await ResolveOriginRepoAsync(session, ct).ConfigureAwait(false);
		string? failure = null;
		try {
			// The page activates this PR's session as this request's answer, landing on its overview.
			await StartReviewAsync(session, async token => {
				string reason = "its head shares no history with it";
				string? mergeBase = null;
				try {
					// The review follows the base branch from here on, so it opens against that same ref.
					await git.FetchBranchAsync(worktree, "origin", baseRef, token).ConfigureAwait(false);
					mergeBase = await git.MergeBaseAsync(worktree, $"origin/{baseRef}", headRef, token).ConfigureAwait(false);
				} catch (GitException ex) {
					reason = ex.Message;
				}

				if (mergeBase is null) {
					failure = $"Opened PR #{number}, but couldn't compute its diff against its base: {reason}";
					return null;
				}

				return new ReviewContext(number, $"PR #{number}", mergeBase, repo, worktree, $"origin/{baseRef}", headRef);
			}, ct).ConfigureAwait(false);
			return failure;
		} catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException) {
			return $"Opened PR #{number}, but couldn't compute its diff: {ex.Message}";
		}
	}

	private static string NoGitHubOrigin(HostSession session) =>
		$"The 'origin' of '{session.Repo.Label}' isn't a recognized GitHub repository.";

	private static Task<RepoRef?> ResolveOriginRepoAsync(HostSession session, CancellationToken ct) =>
		ResolveRemoteRepoAsync(session, "origin", ct);

	// A remote is the same from every worktree of a repo, so it is asked of the session's own.
	private static async Task<RepoRef?> ResolveRemoteRepoAsync(HostSession session, string remote, CancellationToken ct) {
		try {
			string? url = await new GitService()
				.GetRemoteUrlAsync(session.WorkspaceRoot, remote, ct)
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
			+ "NOT run any commands that modify the branch, unless I explicitly ask you to make a change.\n\n"
			+ "Report each finding with the addReviewHint tool so it appears inline at its line; keep your chat "
			+ "reply to an overall summary.";
	}

	internal sealed record PullRequestReference(int Number, string Owner, string Repo);

	internal sealed record PullRequestWire(
		int Number,
		string Title,
		string Author,
		string HeadRef,
		string Url,
		bool Draft);
}
