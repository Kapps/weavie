using Weavie.Core.Git;
using Weavie.Core.Review;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private static readonly TimeSpan PullRequestPollInterval = TimeSpan.FromSeconds(30);

	private void AttachPullRequestStatus(HostSession session) {
		var monitor = new PullRequestStatusMonitor(
			session.Background,
			ct => ResolvePullRequestStatusAsync(session, ct),
			status => session.Bus.BroadcastTarget.Feature("git").Publish("pullRequest", status),
			Task.Delay,
			PullRequestPollInterval);
		session.AttachPullRequestStatus(monitor);
		monitor.UpdateStatus(session.Status.Status);
	}

	private void PushPullRequestStatus(HostSession session) =>
		session.PullRequestStatus.RequestRefresh();

	private void PushPullRequestStatus(HostSession session, MessageTarget target) {
		if (session.PullRequestStatus.Latest is { } latest) {
			target.Feature("git").Publish("pullRequest", latest);
		}

		session.PullRequestStatus.RequestRefresh();
	}

	private async Task<PullRequestStatusSnapshot> ResolvePullRequestStatusAsync(
		HostSession session,
		CancellationToken ct) {
		string? branch = null;
		try {
			branch = await new GitService()
				.GetCurrentBranchAsync(session.WorkspaceRoot, ct)
				.ConfigureAwait(false);
			if (branch is null || await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is not { } headRepo) {
				return new PullRequestStatusSnapshot(branch, null, null);
			}

			if (!headRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
				return Unsupported(branch, headRepo.Host);
			}

			var upstream = await ResolveRemoteRepoAsync("upstream", ct).ConfigureAwait(false);
			var baseRepo = upstream ?? headRepo;
			if (!baseRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
				return Unsupported(branch, baseRepo.Host);
			}

			var found = await _pullRequests.FindForBranchAsync(
				baseRepo,
				headRepo.Owner,
				branch,
				ct).ConfigureAwait(false);
			return new PullRequestStatusSnapshot(
				branch,
				found is null
					? null
					: new PullRequestStatusInfo(
						found.Number,
						_pullRequests.RefUrlBase(baseRepo) + found.Number,
						StateName(found.State)),
				null);
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			return new PullRequestStatusSnapshot(branch, null, ex.Message);
		}
	}

	private static PullRequestStatusSnapshot Unsupported(string branch, string host) =>
		new(branch, null, $"Automatic pull request detection doesn't support {host}.");

	private static string StateName(PullRequestState state) => state switch {
		PullRequestState.Open => "open",
		PullRequestState.Merged => "merged",
		PullRequestState.Closed => "closed",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, "unhandled pull request state"),
	};
}
