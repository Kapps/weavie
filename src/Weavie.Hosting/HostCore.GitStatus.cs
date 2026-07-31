using System.Text.Json;
using Weavie.Core.Git;

namespace Weavie.Hosting;

// Pushes each session's git branch + dirty flag through that session's bus.
public sealed partial class HostCore {
	private void PushGitStatus(HostSession session) =>
		PushGitStatus(session, session.Bus.BroadcastTarget);

	private void PushGitStatus(HostSession session, Messaging.MessageTarget target) {
		string root = session.WorkspaceRoot;
		_ = session.Background.Run(async ct => {
			string? branch = null;
			bool dirty = false;
			try {
				var git = new GitService();
				branch = await git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
				dirty = await git.HasUncommittedChangesAsync(root, ct).ConfigureAwait(false);
			} catch (GitException) {
				// Not a git repo, or git unavailable — the footer shows no branch (the honest "unknown" state).
			}

			ct.ThrowIfCancellationRequested();
			target.Feature("git").Publish("status", new { branch, dirty });
		});
	}
}
