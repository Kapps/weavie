using Weavie.Core.Git;

namespace Weavie.Hosting;

// Publishes each session's Git-owned branch, dirty state, and diff-against-HEAD line totals.
public sealed partial class HostCore {
	private void AttachGitStatus(HostSession session) {
		var monitor = new GitStatusMonitor(
			session.Background,
			ct => ResolveGitStatusAsync(session, ct),
			status => session.Bus.BroadcastTarget.Feature("git").Publish("status", status));
		session.AttachGitStatus(monitor);
		_ = new GitMetadataWatcher(
			session.Background,
			session.WorkspaceRoot,
			monitor.RequestRefresh,
			error => PostForSession(session, () => Notify(session, "warn", error.Message)));
		monitor.RequestRefresh();
	}

	private void PushGitStatus(HostSession session) =>
		session.GitStatus.RequestRefresh();

	private void PushGitStatus(HostSession session, Messaging.MessageTarget target) {
		if (session.GitStatus.Latest is { } latest) {
			target.Feature("git").Publish("status", latest);
		}

		session.GitStatus.RequestRefresh();
	}

	private static async Task<GitStatusSnapshot> ResolveGitStatusAsync(
		HostSession session,
		CancellationToken ct) {
		var git = new GitService();
		GitStatusSummary status;
		try {
			status = await git.GetStatusSummaryAsync(session.WorkspaceRoot, ct).ConfigureAwait(false);
		} catch (GitException) {
			return new GitStatusSnapshot(null, false, null, null, null);
		}

		try {
			var counts = await git.GetHeadDiffLineCountsAsync(session.WorkspaceRoot, ct).ConfigureAwait(false);
			return new GitStatusSnapshot(status.Branch, status.Dirty, counts.Added, counts.Removed, null);
		} catch (GitException ex) {
			return new GitStatusSnapshot(status.Branch, status.Dirty, null, null, ex.Message);
		}
	}
}
