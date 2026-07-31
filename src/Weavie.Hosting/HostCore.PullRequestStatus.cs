using System.Collections.Concurrent;
using Weavie.Core.Git;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private readonly ConcurrentDictionary<Messaging.SessionAddress, PullRequestProbe>
		_pullRequestStatus = new();

	private void PushPullRequestStatus(HostSession session) {
		if (session.Background.Stopping.IsCancellationRequested) {
			return;
		}

		var probe = new PullRequestProbe(session.Background.Stopping);
		_pullRequestStatus.AddOrUpdate(
			session.Address,
			probe,
			(_, previous) => {
				previous.Cancellation.Cancel();
				return probe;
			});
		probe.Task = session.Background.Run(_ => DetectPullRequestAsync(
			session,
			session.Bus.BroadcastTarget,
			probe));
	}

	private void PushPullRequestStatus(
		HostSession session,
		Messaging.MessageTarget target) =>
		_ = session.Background.Run(async ct => {
			var status = await ResolvePullRequestStatusAsync(session, ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			target.Feature("git").Publish("pullRequest", status);
		});

	private async Task DetectPullRequestAsync(
		HostSession session,
		Messaging.MessageTarget target,
		PullRequestProbe probe) {
		try {
			var status = await ResolvePullRequestStatusAsync(
				session,
				probe.Cancellation.Token).ConfigureAwait(false);
			if (probe.Cancellation.IsCancellationRequested
				|| !_pullRequestStatus.TryGetValue(session.Address, out var current)
				|| !ReferenceEquals(current, probe)) {
				return;
			}

			target.Feature("git").Publish("pullRequest", status);
		} catch (OperationCanceledException) when (probe.Cancellation.IsCancellationRequested) {
		} finally {
			if (_pullRequestStatus.TryGetValue(session.Address, out var current)
				&& ReferenceEquals(current, probe)) {
				_pullRequestStatus.TryRemove(session.Address, out _);
			}

			probe.Cancellation.Dispose();
		}
	}

	private async Task<PullRequestStatusSnapshot> ResolvePullRequestStatusAsync(
		HostSession session,
		CancellationToken ct) {
		string? branch = null;
		object? pullRequest = null;
		string? error = null;
		try {
			branch = await new GitService()
				.GetCurrentBranchAsync(session.WorkspaceRoot, ct)
				.ConfigureAwait(false);
			if (branch is not null && await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is { } headRepo) {
				if (!headRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
					error = $"Automatic pull request detection doesn't support {headRepo.Host}.";
				} else {
					var upstream = await ResolveRemoteRepoAsync("upstream", ct).ConfigureAwait(false);
					var baseRepo = upstream ?? headRepo;
					if (!baseRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
						error = $"Automatic pull request detection doesn't support {baseRepo.Host}.";
					} else if (await _pullRequests.FindOpenForBranchAsync(
						baseRepo,
						headRepo.Owner,
						branch,
						ct).ConfigureAwait(false) is { } found) {
						pullRequest = new {
							number = found.Number,
							url = _pullRequests.RefUrlBase(baseRepo) + found.Number,
						};
					}
				}
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			throw;
		} catch (Exception ex) {
			error = ex.Message;
		}

		return new PullRequestStatusSnapshot(branch, pullRequest, error);
	}

	private async Task StopPullRequestStatusAsync() {
		var probes = _pullRequestStatus.Values.ToArray();
		_pullRequestStatus.Clear();
		foreach (var probe in probes) {
			probe.Cancellation.Cancel();
		}

		await Task.WhenAll(probes.Select(probe => probe.Task)).ConfigureAwait(false);
	}

	private sealed class PullRequestProbe {
		public PullRequestProbe(CancellationToken sessionStopping) {
			Cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionStopping);
		}

		public CancellationTokenSource Cancellation { get; }

		public Task Task { get; set; } = Task.CompletedTask;
	}

	private sealed record PullRequestStatusSnapshot(
		string? Branch,
		object? PullRequest,
		string? Error);
}
