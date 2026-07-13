using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Review;

namespace Weavie.Hosting;

// Discovers the active worktree branch's open PR for the structured agent status line. This is separate from
// git-status because forge discovery is authenticated network I/O while branch/dirty state is local and fast.
public sealed partial class HostCore {
	private CancellationTokenSource? _pullRequestStatusCancellation;
	private Task? _pullRequestStatusTask;
	private int _pullRequestStatusVersion;

	private void PushPullRequestStatus() {
		if (_session is not { } session || SlotFor(session) is not { } slot) {
			return;
		}

		var previousTask = _pullRequestStatusTask ?? Task.CompletedTask;
		var previousCancellation = _pullRequestStatusCancellation;
		previousCancellation?.Cancel();
		var cancellation = new CancellationTokenSource();
		_pullRequestStatusCancellation = cancellation;
		int version = ++_pullRequestStatusVersion;
		_pullRequestStatusTask = Task.Run(async () => {
			try {
				await previousTask.ConfigureAwait(false);
			} finally {
				previousCancellation?.Dispose();
			}

			await DetectPullRequestAsync(session, slot.Id, version, cancellation.Token).ConfigureAwait(false);
		});
	}

	private async Task DetectPullRequestAsync(HostSession session, string slot, int version, CancellationToken ct) {
		string? branch = null;
		object? pullRequest = null;
		string? error = null;
		try {
			branch = await new GitService().GetCurrentBranchAsync(session.WorkspaceRoot, ct).ConfigureAwait(false);
			if (branch is not null && await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is { } headRepo) {
				if (!headRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
					error = $"Automatic pull request detection doesn't support {headRepo.Host}.";
				} else {
					var upstream = await ResolveRemoteRepoAsync("upstream", ct).ConfigureAwait(false);
					var baseRepo = upstream ?? headRepo;
					if (!baseRepo.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
						error = $"Automatic pull request detection doesn't support {baseRepo.Host}.";
					} else if (await _pullRequests.FindOpenForBranchAsync(
						baseRepo, headRepo.Owner, branch, ct).ConfigureAwait(false) is { } found) {
						pullRequest = new { number = found.Number, url = _pullRequests.RefUrlBase(baseRepo) + found.Number };
					}
				}
			}
		} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
			return;
		} catch (Exception ex) {
			error = ex.Message;
		}

		if (ct.IsCancellationRequested) {
			return;
		}
		_ui.Post(() => {
			if (ct.IsCancellationRequested || version != _pullRequestStatusVersion || !ReferenceEquals(_session, session)) {
				return;
			}

			_bridge.PostToWeb(JsonSerializer.Serialize(new {
				type = "pull-request-status",
				slot,
				branch,
				pullRequest,
				error,
			}));
		});
	}

	private async Task StopPullRequestStatusAsync() {
		var cancellation = _pullRequestStatusCancellation;
		var task = _pullRequestStatusTask;
		_pullRequestStatusCancellation = null;
		_pullRequestStatusTask = null;
		cancellation?.Cancel();
		if (task is not null) {
			await task.ConfigureAwait(false);
		}

		cancellation?.Dispose();
	}
}
